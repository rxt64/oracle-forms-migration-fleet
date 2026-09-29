using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>
/// One prepared artifact the server admitted, recorded by the server and never by the estate.
///
/// Every field here is something this host observed for itself: <paramref name="ModuleContentSha256"/>
/// is the digest it computed over the module bytes in its own session copy before the gateway was
/// called, and <paramref name="ArtifactSha256"/> is the digest of the bytes it decoded and verified.
/// Nothing in the record is copied from a document the caller can supply, which is the only reason it
/// can be used to decide whether a file found in a source tree is an admitted extraction or an upload.
/// </summary>
public sealed record PreparedSourceClaim(
    string SourceRoot,
    string ModuleAlias,
    string ModuleIdentity,
    string ModuleContentSha256,
    string ArtifactPath,
    string ArtifactSha256,
    int ArtifactByteCount,
    string ProvenancePath,
    string ProvenanceSha256,
    int ProvenanceByteCount,
    string SourceEnvironmentId,
    int ProfileVersion,
    string ProfileHash,
    string ExpectedFormsRelease,
    string Gateway,
    DateTimeOffset PreparedUtc);

/// <summary>
/// The opaque stand-in for "which tenant, project and principal this session copy belongs to".
///
/// The ledger has to record WHO a claim was prepared for, or a claim written under one project's
/// authority could be consumed by a run carried out under another's. Writing the owner in the clear
/// would put a tenant identifier, a project identifier and a principal's object identifier into a file
/// that sits on the same volume as the customer's source, so the ledger records a domain-separated
/// digest of the owner instead. It is derived, never parsed: this type can say whether two owners are
/// the same and can say nothing else about either of them.
///
/// It is deliberately NOT the workspace snapshot digest. That digest changes every time the fleet writes
/// into the copy, and it is the identity of the BYTES rather than of the authority they were read under.
/// </summary>
public static class PreparedSourceOwnerBinding
{
    private const string Domain = "fleet.prepared-source-trust.owner/1\n";

    /// <summary>The binding for a workspace owner, or an empty string when there is no owner to bind to.</summary>
    public static string Derive(string? workspaceOwner) =>
        string.IsNullOrWhiteSpace(workspaceOwner)
            ? string.Empty
            : Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Domain + workspaceOwner)));

    public static bool IsWellFormed(string? binding) =>
        binding is { Length: 64 } && binding.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Ordinal comparison of two bindings, where an absent or malformed binding matches nothing.</summary>
    public static bool Matches(string? expected, string? recorded) =>
        IsWellFormed(expected) && IsWellFormed(recorded) && string.Equals(expected, recorded, StringComparison.Ordinal);
}

/// <summary>
/// The claims a single session workspace accumulated, as they sit on the server's own disk.
///
/// <paramref name="OwnerBinding"/> is the opaque owner the claims were prepared for. A ledger carrying
/// claims and no binding is refused rather than migrated: the binding is what stops a claim being read
/// under an authority it was never granted under, and inventing one for an older document would be this
/// server asserting a fact it does not have.
/// </summary>
public sealed record PreparedSourceTrustLedger(
    string Record,
    string WorkspaceId,
    string OwnerBinding,
    IReadOnlyList<PreparedSourceClaim> Claims);

/// <summary>
/// Outcome of consulting the ledger. A workspace that never prepared anything reads as an empty ledger
/// and no error; a ledger that exists and cannot be believed reads as an error and no ledger, because
/// reconstructing trust from whatever is left in the source tree is exactly the forgery this guards.
/// </summary>
public sealed record PreparedSourceTrustRead(PreparedSourceTrustLedger? Ledger, string? Error);

/// <summary>
/// The server-owned record of which prepared artifacts are real.
///
/// The problem it solves: an operator's session copy comes from an uploaded archive or a public
/// repository, and either can contain a <c>.fleet-source/extraction/ORDERS.fmb.forms-ir.json</c> beside
/// a hand-written provenance document claiming any release, any profile and any digest. Nothing inside
/// the tree can distinguish that from an artifact the source gateway actually returned, because every
/// byte of it is caller-supplied. So the record lives outside the tree entirely.
///
/// The ledger sits in a sibling directory of the workspace folder rather than inside it. Archive
/// extraction resolves every entry against the workspace folder plus a separator and refuses anything
/// that lands elsewhere, and a clone writes only under that folder, so neither can reach this path. It
/// is on the same volume as the workspace it describes and has the same lifetime: it survives a host
/// restart exactly as far as the source copy does, and where the copy is gone there is nothing left to
/// trust anyway.
///
/// The ledger is the whole of the trust decision. An artifact with no claim is never consumed, so a
/// write that failed part-way through leaves files that read as forgeries rather than as source.
/// </summary>
public static class PreparedSourceTrustStore
{
    public const string RecordType = "fleet.prepared-source-trust/1";

    /// <summary>Directory beside the workspace folders. Never a workspace identifier, so it cannot collide with one.</summary>
    public const string LedgerDirectory = ".prepared-trust";

    /// <summary>Folder inside a selected source root that admitted extraction artifacts are written into.</summary>
    public const string PreparedFolder = ".fleet-source/extraction";

    /// <summary>Suffix of an admitted Forms intermediate representation.</summary>
    public const string ArtifactSuffix = ".forms-ir.json";

    /// <summary>Suffix of the readable provenance document written beside one.</summary>
    public const string ProvenanceSuffix = ".provenance.json";

    public const int MaxClaims = 1_024;

    private const long MaxLedgerBytes = 8L * 1024 * 1024;

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    /// <summary>
    /// Where the ledger for a workspace lives, or null when the supplied root is not a workspace folder
    /// with a parent to put one beside.
    /// </summary>
    public static string? LedgerPath(string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Path.IsPathRooted(workspaceRoot))
        {
            return null;
        }

        string full = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar);
        string leaf = Path.GetFileName(full);

        return Path.GetDirectoryName(full) is { Length: > 0 } parent
            && leaf is { Length: > 0 and <= 128 }
            && leaf.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
                ? Path.Combine(parent, LedgerDirectory, $"{leaf}.json")
                : null;
    }

    /// <summary>
    /// Reads the claims recorded for a workspace, checking that the document is the one this server
    /// wrote for this workspace AND for the owner the caller is acting as.
    ///
    /// <paramref name="expectedOwnerBinding"/> is derived by the caller from the run's own platform
    /// binding, never from anything inside the session copy. A ledger that carries claims is refused when
    /// the caller brought no binding, so a consumer that cannot say whose authority it is acting under
    /// reads no claim at all rather than every claim it finds.
    /// </summary>
    public static PreparedSourceTrustRead Read(string? workspaceRoot, string? expectedOwnerBinding)
    {
        if (LedgerPath(workspaceRoot) is not { } path)
        {
            return new PreparedSourceTrustRead(
                null,
                "The prepared-source trust record for this workspace could not be located, so no extracted artifact in it can be " +
                "distinguished from an uploaded file. Nothing was trusted.");
        }

        string workspaceId = Path.GetFileName(Path.GetFullPath(workspaceRoot!).TrimEnd(Path.DirectorySeparatorChar));

        if (!File.Exists(path))
        {
            return new PreparedSourceTrustRead(
                new PreparedSourceTrustLedger(RecordType, workspaceId, expectedOwnerBinding ?? string.Empty, []), null);
        }

        byte[] bytes;
        try
        {
            using FileStream stream = File.OpenRead(path);
            if (stream.Length > MaxLedgerBytes)
            {
                return Refuse("The prepared-source trust record is larger than this server writes, so it was not read.");
            }

            bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Refuse("The prepared-source trust record could not be read, so no prepared artifact was trusted.");
        }

        PreparedSourceTrustLedger? ledger;
        try
        {
            ledger = JsonSerializer.Deserialize<PreparedSourceTrustLedger>(bytes, s_json);
        }
        catch (JsonException)
        {
            return Refuse("The prepared-source trust record is not a document this server wrote.");
        }

        if (ledger is null
            || !string.Equals(ledger.Record, RecordType, StringComparison.Ordinal)
            || !string.Equals(ledger.WorkspaceId, workspaceId, StringComparison.Ordinal)
            || ledger.Claims is null
            || ledger.Claims.Count > MaxClaims)
        {
            return Refuse(
                "The prepared-source trust record does not identify itself as this workspace's record, so it was not read. A ledger " +
                "belongs to the copy it was written for.");
        }

        // An empty ledger vouches for nothing, so it is readable without an owner. The moment it carries a
        // claim, the authority that claim was prepared under has to be the authority now reading it.
        if (ledger.Claims.Count > 0 && !PreparedSourceOwnerBinding.Matches(expectedOwnerBinding, ledger.OwnerBinding))
        {
            return Refuse(
                "The prepared-source trust record was written for a different project, tenant, or principal than the one this " +
                "operation is acting for, or this operation established none. A prepared artifact is readable only under the " +
                "authority it was prepared under, so nothing was trusted.");
        }

        foreach (PreparedSourceClaim claim in ledger.Claims)
        {
            if (Malformed(claim) is { } rejection)
            {
                return Refuse($"The prepared-source trust record carries an entry this server would not have written: {rejection}");
            }
        }

        return new PreparedSourceTrustRead(ledger, null);

        static PreparedSourceTrustRead Refuse(string reason) => new(null, reason);
    }

    /// <summary>
    /// Records one claim, or reports why it was not recorded. A claim for an artifact path the ledger
    /// already carries is refused rather than replaced: a prepared artifact is written once, and letting
    /// a second preparation silently re-point an existing path would change what an earlier run read.
    ///
    /// The caller holds a per-workspace lock across this call and the rename that follows it, so the
    /// read-modify-write below is not a lost-update window for a second module prepared at the same time.
    /// </summary>
    internal static bool TryAppend(string workspaceRoot, string ownerBinding, PreparedSourceClaim claim, out string error)
    {
        ArgumentNullException.ThrowIfNull(claim);

        if (!PreparedSourceOwnerBinding.IsWellFormed(ownerBinding))
        {
            error =
                "The prepared artifact could not be recorded: this server established no owner for the session copy, so the " +
                "claim would not name the authority it was prepared under.";
            return false;
        }

        if (Malformed(claim) is { } rejection)
        {
            error = $"The prepared artifact could not be recorded: {rejection}";
            return false;
        }

        PreparedSourceTrustRead read = Read(workspaceRoot, ownerBinding);
        if (read.Ledger is not { } ledger)
        {
            error = read.Error ?? "The prepared-source trust record could not be read.";
            return false;
        }

        if (ledger.Claims.Count >= MaxClaims)
        {
            error = $"This session copy already holds {MaxClaims.ToString(CultureInfo.InvariantCulture)} prepared artifacts.";
            return false;
        }

        if (ledger.Claims.Any(existing =>
                string.Equals(existing.ArtifactPath, claim.ArtifactPath, StringComparison.Ordinal)
                || string.Equals(existing.ProvenancePath, claim.ProvenancePath, StringComparison.Ordinal)))
        {
            error =
                "This module has already been prepared into this source copy. A prepared artifact is written once, so nothing was " +
                "overwritten; take a fresh copy of the source to prepare it again.";
            return false;
        }

        return TryWrite(workspaceRoot, ledger with { OwnerBinding = ownerBinding, Claims = [.. ledger.Claims, claim] }, out error);
    }

    /// <summary>
    /// Withdraws a claim. Used to roll back a commit whose files did not all reach their final names, so
    /// the ledger never describes an artifact the next reader will not find.
    /// </summary>
    internal static bool TryWithdraw(string workspaceRoot, string ownerBinding, string artifactPath, out string error)
    {
        PreparedSourceTrustRead read = Read(workspaceRoot, ownerBinding);
        if (read.Ledger is not { } ledger)
        {
            error = read.Error ?? "The prepared-source trust record could not be read.";
            return false;
        }

        return TryWrite(
            workspaceRoot,
            ledger with
            {
                Claims = [.. ledger.Claims.Where(claim => !string.Equals(claim.ArtifactPath, artifactPath, StringComparison.Ordinal))],
            },
            out error);
    }

    /// <summary>
    /// Replaces the ledger in one step. The document is written to a temporary name beside it and moved
    /// over the old one, so a reader sees either the previous ledger or the new one and never a partial
    /// document that would read as a shorter list of admitted artifacts.
    /// </summary>
    private static bool TryWrite(string workspaceRoot, PreparedSourceTrustLedger ledger, out string error)
    {
        if (LedgerPath(workspaceRoot) is not { } path)
        {
            error = "The prepared-source trust record for this workspace could not be located.";
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
            error = "The prepared-source trust record could not be written, so the artifact was not admitted.";
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

    private static string? Malformed(PreparedSourceClaim? claim) => claim switch
    {
        null => "the entry is absent.",
        { ArtifactByteCount: <= 0 } or { ProvenanceByteCount: <= 0 } => "it records an empty artifact.",
        { ProfileVersion: <= 0 } => "it records no source profile version.",
        _ when !IsSha256(claim.ModuleContentSha256) => "it records no module content digest.",
        _ when !IsSha256(claim.ArtifactSha256) || !IsSha256(claim.ProvenanceSha256) => "it records no artifact digest.",
        _ when claim.SourceRoot is null => "it records no source folder.",
        _ when claim.ModuleAlias is not { Length: > 0 } => "it names no module.",
        _ when claim.ArtifactPath is not { Length: > 0 } || claim.ProvenancePath is not { Length: > 0 } => "it names no file.",
        _ when claim.SourceEnvironmentId is not { Length: > 0 } => "it names no source environment.",
        _ when claim.ProfileHash is not { Length: > 0 } => "it records no source profile hash.",
        _ when claim.ExpectedFormsRelease is not { Length: > 0 } => "it records no expected Oracle Forms release.",
        _ => null,
    };

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
