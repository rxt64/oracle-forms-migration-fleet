// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>
/// One admitted Oracle schema extraction, recorded by the server and never by the estate.
///
/// Like the module claim beside it, every field is something this host observed: the artifact digest is
/// the digest of the bytes it decoded and verified, and the statement digests are over the exact slices
/// of canonical DDL it wrote. The counts are the worker's own coverage, copied through so a reader can
/// see what the catalog reported without re-parsing the artifact.
/// </summary>
public sealed record PreparedSchemaClaim(
    string SourceRoot,
    string SourceEnvironmentId,
    int ProfileVersion,
    string ProfileHash,
    IReadOnlyList<string> Schemas,
    string ArtifactSha256,
    int ArtifactByteCount,
    string SchemaDdlPath,
    string SchemaDdlSha256,
    string? ProgramUnitPath,
    string? ProgramUnitSha256,
    string ProvenancePath,
    string ProvenanceSha256,
    int Tables,
    int Columns,
    int Constraints,
    int Sequences,
    int ProgramUnits,
    string Gateway,
    DateTimeOffset PreparedUtc);

/// <summary>
/// The claims a single session workspace accumulated for its source database.
///
/// <paramref name="OwnerBinding"/> carries the same opaque owner as the module ledger beside it and for
/// the same reason: statements prepared under one project's authority are not readable under another's.
/// </summary>
public sealed record PreparedSchemaTrustLedger(
    string Record,
    string WorkspaceId,
    string OwnerBinding,
    IReadOnlyList<PreparedSchemaClaim> Claims);

public sealed record PreparedSchemaTrustRead(PreparedSchemaTrustLedger? Ledger, string? Error);

/// <summary>
/// The server-owned record of which prepared schema statements are real.
///
/// It exists for the same reason as the module ledger and is deliberately separate from it rather than an
/// extension of it: a <c>.sql</c> file in an operator's session copy is indistinguishable from one their
/// archive carried, so the decision about which ones this fleet produced cannot live anywhere the archive
/// could reach. The document therefore sits in the same sibling directory of the workspace folder, which
/// archive extraction and clone both refuse to write into, under its own name.
///
/// A file with no claim is never read as prepared output, so a commit that failed part way through leaves
/// statements that read as an upload rather than as extraction this host vouches for.
/// </summary>
public static class PreparedSchemaTrustStore
{
    public const string RecordType = "fleet.prepared-schema-trust/1";

    /// <summary>Folder inside a selected source root that admitted schema statements are written into.</summary>
    public const string PreparedFolder = PreparedSourceTrustStore.PreparedFolder;

    public const string SchemaDdlSuffix = ".schema.sql";

    public const string ProgramUnitSuffix = ".program-units.sql";

    public const string ProvenanceSuffix = ".schema.provenance.json";

    public const int MaxClaims = 64;

    private const long MaxLedgerBytes = 8L * 1024 * 1024;

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    /// <summary>Where the schema ledger for a workspace lives, beside the module ledger and never inside the copy.</summary>
    public static string? LedgerPath(string? workspaceRoot) =>
        PreparedSourceTrustStore.LedgerPath(workspaceRoot) is { } modulePath
            ? Path.Combine(
                Path.GetDirectoryName(modulePath)!,
                $"{Path.GetFileNameWithoutExtension(modulePath)}.schema.json")
            : null;

    public static PreparedSchemaTrustRead Read(string? workspaceRoot, string? expectedOwnerBinding)
    {
        if (LedgerPath(workspaceRoot) is not { } path)
        {
            return new PreparedSchemaTrustRead(
                null,
                "The prepared-schema trust record for this workspace could not be located, so no schema statements in it can " +
                "be distinguished from uploaded files. Nothing was trusted.");
        }

        string workspaceId = Path.GetFileName(Path.GetFullPath(workspaceRoot!).TrimEnd(Path.DirectorySeparatorChar));

        if (!File.Exists(path))
        {
            return new PreparedSchemaTrustRead(
                new PreparedSchemaTrustLedger(RecordType, workspaceId, expectedOwnerBinding ?? string.Empty, []), null);
        }

        byte[] bytes;
        try
        {
            using FileStream stream = File.OpenRead(path);
            if (stream.Length > MaxLedgerBytes)
            {
                return Refuse("The prepared-schema trust record is larger than this server writes, so it was not read.");
            }

            bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Refuse("The prepared-schema trust record could not be read, so no prepared schema was trusted.");
        }

        PreparedSchemaTrustLedger? ledger;
        try
        {
            ledger = JsonSerializer.Deserialize<PreparedSchemaTrustLedger>(bytes, s_json);
        }
        catch (JsonException)
        {
            return Refuse("The prepared-schema trust record is not a document this server wrote.");
        }

        if (ledger is null
            || !string.Equals(ledger.Record, RecordType, StringComparison.Ordinal)
            || !string.Equals(ledger.WorkspaceId, workspaceId, StringComparison.Ordinal)
            || ledger.Claims is null
            || ledger.Claims.Count > MaxClaims)
        {
            return Refuse(
                "The prepared-schema trust record does not identify itself as this workspace's record, so it was not read. A " +
                "ledger belongs to the copy it was written for.");
        }

        if (ledger.Claims.Count > 0 && !PreparedSourceOwnerBinding.Matches(expectedOwnerBinding, ledger.OwnerBinding))
        {
            return Refuse(
                "The prepared-schema trust record was written for a different project, tenant, or principal than the one this " +
                "operation is acting for, or this operation established none. Prepared statements are readable only under the " +
                "authority they were prepared under, so nothing was trusted.");
        }

        foreach (PreparedSchemaClaim claim in ledger.Claims)
        {
            if (Malformed(claim) is { } rejection)
            {
                return Refuse($"The prepared-schema trust record carries an entry this server would not have written: {rejection}");
            }
        }

        return new PreparedSchemaTrustRead(ledger, null);

        static PreparedSchemaTrustRead Refuse(string reason) => new(null, reason);
    }

    /// <summary>
    /// Records one claim. A claim naming a path the ledger already carries is refused rather than
    /// replaced: prepared statements are written once, and re-pointing a path would change what an
    /// earlier run read.
    /// </summary>
    internal static bool TryAppend(string workspaceRoot, string ownerBinding, PreparedSchemaClaim claim, out string error)
    {
        ArgumentNullException.ThrowIfNull(claim);

        if (!PreparedSourceOwnerBinding.IsWellFormed(ownerBinding))
        {
            error =
                "The prepared schema could not be recorded: this server established no owner for the session copy, so the " +
                "claim would not name the authority it was prepared under.";
            return false;
        }

        if (Malformed(claim) is { } rejection)
        {
            error = $"The prepared schema could not be recorded: {rejection}";
            return false;
        }

        PreparedSchemaTrustRead read = Read(workspaceRoot, ownerBinding);
        if (read.Ledger is not { } ledger)
        {
            error = read.Error ?? "The prepared-schema trust record could not be read.";
            return false;
        }

        if (ledger.Claims.Count >= MaxClaims)
        {
            error = $"This session copy already holds {MaxClaims.ToString(CultureInfo.InvariantCulture)} prepared schema extractions.";
            return false;
        }

        if (ledger.Claims.Any(existing => Paths(existing).Intersect(Paths(claim), StringComparer.Ordinal).Any()))
        {
            error =
                "This source environment's schema has already been prepared into this source copy. Prepared statements are " +
                "written once, so nothing was overwritten; take a fresh copy of the source to prepare it again.";
            return false;
        }

        return TryWrite(workspaceRoot, ledger with { OwnerBinding = ownerBinding, Claims = [.. ledger.Claims, claim] }, out error);
    }

    /// <summary>Withdraws a claim whose files did not all reach their final names.</summary>
    internal static bool TryWithdraw(string workspaceRoot, string ownerBinding, string schemaDdlPath, out string error)
    {
        PreparedSchemaTrustRead read = Read(workspaceRoot, ownerBinding);
        if (read.Ledger is not { } ledger)
        {
            error = read.Error ?? "The prepared-schema trust record could not be read.";
            return false;
        }

        return TryWrite(
            workspaceRoot,
            ledger with
            {
                Claims = [.. ledger.Claims.Where(claim => !string.Equals(claim.SchemaDdlPath, schemaDdlPath, StringComparison.Ordinal))],
            },
            out error);
    }

    private static IEnumerable<string> Paths(PreparedSchemaClaim claim)
    {
        yield return claim.SchemaDdlPath;
        yield return claim.ProvenancePath;
        if (claim.ProgramUnitPath is { Length: > 0 } programUnits)
        {
            yield return programUnits;
        }
    }

    private static bool TryWrite(string workspaceRoot, PreparedSchemaTrustLedger ledger, out string error)
    {
        if (LedgerPath(workspaceRoot) is not { } path)
        {
            error = "The prepared-schema trust record for this workspace could not be located.";
            return false;
        }

        string temporary = $"{path}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}.tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using (FileStream stream = File.Create(temporary))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(ledger, s_json));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Discard(temporary);
            error = "The prepared-schema trust record could not be written, so the statements were not admitted.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static void Discard(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string? Malformed(PreparedSchemaClaim? claim) => claim switch
    {
        null => "the entry is absent.",
        { ArtifactByteCount: <= 0 } => "it records an empty artifact.",
        { ProfileVersion: <= 0 } => "it records no source profile version.",
        _ when !IsSha256(claim.ArtifactSha256) => "it records no artifact digest.",
        _ when !IsSha256(claim.SchemaDdlSha256) || !IsSha256(claim.ProvenanceSha256) => "it records no statement digest.",
        _ when claim.ProgramUnitPath is { Length: > 0 } != IsSha256(claim.ProgramUnitSha256) =>
            "it records a program-unit file without its digest, or a digest without a file.",
        _ when claim.SourceRoot is null => "it records no source folder.",
        _ when claim.Schemas is not { Count: > 0 } => "it names no schema.",
        _ when claim.SchemaDdlPath is not { Length: > 0 } || claim.ProvenancePath is not { Length: > 0 } => "it names no file.",
        _ when claim.SourceEnvironmentId is not { Length: > 0 } => "it names no source environment.",
        _ when claim.ProfileHash is not { Length: > 0 } => "it records no source profile hash.",
        _ => null,
    };

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
