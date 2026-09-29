// Copyright (c) Microsoft. All rights reserved.

using System.Security.Cryptography;
using System.Text.Json;

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>
/// The wire contract between this web host and the separately deployed source gateway.
///
/// The gateway is the only thing that may run a native Forms worker or open an Oracle connection. This
/// process never starts either: it posts a small, fully bounded JSON document to one fixed configured
/// authority and reads one bounded JSON document back.
///
/// Two properties make the contract worth writing down rather than guessing at:
///
/// 1. <b>The response carries artifact BYTES, never a path.</b> The native worker writes its
///    intermediate representation to its own disk and reports
///    <c>intermediateRepresentationPath</c>; that path is meaningless here and, if it were honoured,
///    would be a remote-controlled filesystem read on this host. The gateway is therefore required to
///    inline the artifact as base64 with its own digest, and this host verifies the digest over the
///    bytes it decoded.
/// 2. <b>Nothing in a response may claim adjudication.</b> The artifact is EXTRACTION output. It is
///    admitted only if it carries the native worker's generator and does not declare the host's
///    normalization fields, so a compromised gateway cannot hand back a document that later reads as
///    already normalized.
///
/// PROTOCOL, as a gateway implementer must build it:
/// <code>
/// POST {authority}/source/forms-module/extract
///   Authorization: Bearer &lt;token for the configured scope, audience = the gateway&gt;
///   Content-Type:  application/json; charset=utf-8
///   Body (&lt;= 8 KiB):   SourceGatewayFormsModuleRequest
///   200 application/json: SourceGatewayFormsModuleResponse
///
/// POST {authority}/source/oracle-schema/extract
///   Body (&lt;= 8 KiB):   SourceGatewayOracleSchemaRequest
///   200 application/json: SourceGatewayOracleSchemaResponse
/// </code>
/// Any other status, any redirect, any other media type, and any response larger than
/// <see cref="MaxResponseBytes"/> is a failure. There is no retry that could make a malformed or partial
/// response safe, so there is none.
/// </summary>
public static class SourceGatewayProtocol
{
    /// <summary>The only request and response schema version this build speaks.</summary>
    public const int SchemaVersion = 1;

    public const string FormsModuleExtractPath = "source/forms-module/extract";

    public const string OracleSchemaExtractPath = "source/oracle-schema/extract";

    /// <summary>Media type of an inlined Forms intermediate representation.</summary>
    public const string FormsIrMediaType = "application/vnd.oracle-forms-migration-fleet.forms-ir+json";

    /// <summary>Media type of an inlined Oracle schema artifact.</summary>
    public const string OracleSchemaMediaType = "application/vnd.oracle-forms-migration-fleet.oracle-schema+json";

    /// <summary>Hard cap on a request body. The request carries identifiers and digests and nothing else.</summary>
    public const int MaxRequestBytes = 8 * 1024;

    /// <summary>Hard cap on a whole response body, enforced while reading rather than after.</summary>
    public const int MaxResponseBytes = 24 * 1024 * 1024;

    /// <summary>Hard cap on one decoded artifact.</summary>
    public const int MaxArtifactBytes = 16 * 1024 * 1024;

    public const int MaxFindings = 64;

    public const int MaxFindingCharacters = 512;

    public const int MaxCapabilities = 32;

    /// <summary>Clock skew allowed between the gateway's extraction stamp and this host.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(10);

    /// <summary>The generator an extraction artifact must declare. Deliberately not the host's.</summary>
    public const string ExtractedIrGenerator = "oracle-forms-migration-fleet/source-worker-native";

    /// <summary>The IR body version this build admits.</summary>
    public const string ExtractedIrSchemaVersion = "3";

    /// <summary>The generator an Oracle schema artifact must declare. Deliberately not the host's.</summary>
    public const string ExtractedSchemaGenerator = "oracle-forms-migration-fleet/source-worker-oracle-catalog";

    /// <summary>The schema artifact body version this build admits.</summary>
    public const string ExtractedSchemaBodyVersion = "1";

    /// <summary>
    /// The line the worker's emitter writes before a schema's program units, and the only boundary this
    /// host will split a canonical DDL on. A split that cannot be located is a refusal, never a guess.
    /// </summary>
    public const string ProgramUnitSectionMarker = "ALTER SESSION SET CURRENT_SCHEMA = ";

    /// <summary>Schemas one response may cover, matching the profile's own ceiling.</summary>
    public const int MaxSchemas = 32;

    /// <summary>
    /// Ceiling on one schema artifact's object inventory, and on any single coverage count it reports.
    /// It bounds the reconciliation before the counts are believed, so an artifact cannot make this host
    /// allocate against a number it invented.
    /// </summary>
    public const int MaxSchemaObjects = 65536;

    /// <summary>Ceiling on canonical statements in one artifact's schema DDL.</summary>
    public const int MaxSchemaStatements = 262144;

    /// <summary>Longest Oracle identifier this host will read out of an artifact.</summary>
    public const int MaxSchemaIdentifierCharacters = 128;

    /// <summary>
    /// Fields that only the host's normalization phase may write. An extraction artifact declaring any of
    /// them is refused: admitting it would let the gateway skip the adjudication those fields stand for.
    /// </summary>
    public static readonly string[] AdjudicationFields = ["normalized", "sourceRoot"];

    /// <summary>Serialization for both directions. Enum-free on the wire; every status is a string.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
}

/// <summary>
/// Who this host is acting for, as the SERVER decided it.
///
/// It is never read from a browser body, a request field, or anything a caller can influence: the tenant
/// comes from the authenticated principal's own token and the project comes from the membership check
/// that already succeeded. It travels with every gateway call so the gateway can refuse a source
/// environment that was registered for a different tenant or project, which is the one thing a shared
/// app-only managed identity cannot establish on its own — every project in the tenant presents the same
/// application identity, so without this the gateway would serve any of them any registered source.
///
/// The gateway ECHOES it back. That echo is correlation and never authorization: this host compares the
/// echo with the scope it derived itself, and a gateway that returns a different scope has its whole
/// response discarded rather than believed.
/// </summary>
public sealed record SourceGatewayAuthorizationScope(string TenantId, string ProjectId)
{
    public const int MaxPartLength = 128;

    /// <summary>True when both parts are bounded identifiers this protocol is willing to put on the wire.</summary>
    public static bool IsWellFormed(SourceGatewayAuthorizationScope? scope) =>
        scope is not null && IsPart(scope.TenantId) && IsPart(scope.ProjectId);

    /// <summary>Ordinal equality of both parts. A missing scope never matches anything.</summary>
    public bool Matches(SourceGatewayAuthorizationScope? other) =>
        other is not null &&
        string.Equals(TenantId, other.TenantId, StringComparison.Ordinal) &&
        string.Equals(ProjectId, other.ProjectId, StringComparison.Ordinal);

    private static bool IsPart(string? value) =>
        value is { Length: > 0 and <= MaxPartLength } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');
}

/// <summary>Terminal states a gateway may report. Anything else is a malformed response.</summary>
public static class SourceGatewayStatus
{
    /// <summary>The module was opened and an artifact is inlined. The only status that carries bytes.</summary>
    public const string Extracted = "Extracted";

    /// <summary>The gateway is reachable but a prerequisite is unmet. No module was opened.</summary>
    public const string BlockedPrerequisite = "BlockedPrerequisite";

    /// <summary>The request was refused before any work started.</summary>
    public const string Rejected = "Rejected";

    /// <summary>Work started and failed. No artifact.</summary>
    public const string ExtractionFailed = "ExtractionFailed";

    public static bool IsKnown(string? status) =>
        status is Extracted or BlockedPrerequisite or Rejected or ExtractionFailed;
}

/// <summary>
/// One artifact, inline. <paramref name="Base64"/> is the whole artifact;
/// <paramref name="Sha256"/> is the digest of the DECODED bytes, which the caller recomputes.
/// </summary>
public sealed record SourceGatewayArtifact(string MediaType, string Sha256, string Base64);

/// <summary>A capability the gateway actually exercised or refused, mirroring the worker's own shape.</summary>
public sealed record SourceGatewayCapability(
    string Id,
    string State,
    string Prerequisite,
    string? RequiredRelease,
    string? RequiredArchitecture,
    string? RequiredHost,
    string Remediation,
    string? Observed);

/// <summary>What the gateway observed about the native libraries it loaded. Never a path or a credential.</summary>
public sealed record SourceGatewayNativeEvidence(
    string LibraryAlias,
    string? FileVersion,
    string? ProductVersion,
    string LibrarySha256,
    string DefinitionHeaderSha256,
    IReadOnlyList<string> ResolvedExports,
    IReadOnlyList<string> ResolvedConstants);

/// <summary>
/// A bounded request for one module that already sits under the gateway's own trusted input root.
///
/// There is no path, no command, no library name and no connect string, because the gateway resolves
/// every location from its own configuration. <paramref name="ModuleAlias"/> is one file name the
/// operator already placed there, and <paramref name="ExpectedContentSha256"/> pins the bytes this host
/// computed from its own session copy, so the two sides are talking about the same module or neither
/// proceeds.
/// </summary>
public sealed record SourceGatewayFormsModuleRequest(
    int SchemaVersion,
    string SourceEnvironmentId,
    string ExpectedFormsRelease,
    int ProfileVersion,
    string ProfileHash,
    string ModuleAlias,
    string ExpectedContentSha256,
    SourceGatewayAuthorizationScope Scope);

/// <summary>
/// The extraction result. Every correlation field is echoed so a response cannot be replayed against a
/// different source environment, profile version, or module.
/// </summary>
public sealed record SourceGatewayFormsModuleResponse(
    int SchemaVersion,
    string Status,
    string SourceEnvironmentId,
    string ExpectedFormsRelease,
    int ProfileVersion,
    string ProfileHash,
    string ModuleAlias,
    DateTimeOffset ExtractedUtc,
    string? ModuleIdentity,
    string? ObservedContentSha256,
    SourceGatewayArtifact? IntermediateRepresentation,
    SourceGatewayNativeEvidence? Native,
    IReadOnlyList<SourceGatewayCapability> Capabilities,
    IReadOnlyList<string> Findings,
    SourceGatewayAuthorizationScope? Scope = null);

/// <summary>
/// A bounded request for the Oracle schema of the allowlisted schemas on the profile.
///
/// The schema list is the profile's own allowlist, which is stored server-side and never read from a
/// browser body. No credential travels: the gateway holds its own connection identity.
/// </summary>
public sealed record SourceGatewayOracleSchemaRequest(
    int SchemaVersion,
    string SourceEnvironmentId,
    int ProfileVersion,
    string ProfileHash,
    IReadOnlyList<string> SchemaAllowlist,
    SourceGatewayAuthorizationScope Scope);

public sealed record SourceGatewayOracleSchemaResponse(
    int SchemaVersion,
    string Status,
    string SourceEnvironmentId,
    int ProfileVersion,
    string ProfileHash,
    DateTimeOffset ExtractedUtc,
    string? SnapshotHash,
    SourceGatewayArtifact? SchemaArtifact,
    IReadOnlyList<SourceGatewayCapability> Capabilities,
    IReadOnlyList<string> Findings,
    SourceGatewayAuthorizationScope? Scope = null);

/// <summary>
/// What admitting a gateway response produced. <paramref name="Artifact"/> is set only when
/// <paramref name="Admitted"/> is true, and is the exact bytes whose digest was verified.
/// </summary>
/// <param name="Reported">
/// The status the gateway reported, as this host is willing to repeat it. A malformed response reports
/// <see cref="SourceGatewayStatus.Rejected"/> rather than the string it claimed, because a claim that
/// failed validation is not a fact about the source.
/// </param>
public sealed record SourceGatewayAdmission(
    bool Admitted,
    string Reported,
    byte[]? Artifact,
    string? ModuleIdentity,
    string Reason);

/// <summary>
/// What the canonical artifact actually contains, read out of the document the gateway returned and
/// nothing else.
///
/// <paramref name="SchemaDdl"/> and <paramref name="ProgramUnitSql"/> are slices of the one canonical
/// DDL string the worker emitted from observed catalog rows. They are separated because this host's
/// source indexer classifies by file name, and the two halves are different evidence kinds; they are
/// never rewritten, reordered or synthesized. <paramref name="ProgramUnitSql"/> is null only when the
/// artifact reported no program unit at all.
/// </summary>
public sealed record SourceGatewaySchemaProjection(
    IReadOnlyList<string> Schemas,
    string Provider,
    string SchemaDdl,
    string? ProgramUnitSql,
    int Tables,
    int Columns,
    int Constraints,
    int Sequences,
    int ProgramUnits);

/// <summary>
/// What admitting an Oracle schema response produced. <paramref name="Artifact"/> and
/// <paramref name="Projection"/> are set together and only when <paramref name="Admitted"/> is true.
/// </summary>
public sealed record SourceGatewaySchemaAdmission(
    bool Admitted,
    string Reported,
    byte[]? Artifact,
    string? SnapshotHash,
    SourceGatewaySchemaProjection? Projection,
    string Reason);

/// <summary>
/// Decides whether a gateway response may be believed, and hands back only bytes it verified itself.
///
/// This is deliberately a pure function over the two documents and the server clock. It is the single
/// place where a remote answer becomes a local fact, and it is offline and exhaustively testable for
/// exactly that reason. Every check below failed closed in a test before it was written down here.
/// </summary>
public static class SourceGatewayValidation
{
    public static SourceGatewayAdmission AdmitFormsModule(
        SourceGatewayFormsModuleRequest request,
        SourceGatewayFormsModuleResponse? response,
        DateTimeOffset serverUtc)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The scope is the server's own answer to "who is this for", so a request that never carried one
        // is a defect in this host, not a fact about the gateway. It is checked before the response is
        // looked at so a malformed scope can never be compared into a match.
        if (!SourceGatewayAuthorizationScope.IsWellFormed(request.Scope))
        {
            return Refuse(
                "This host did not derive a server-owned authorization scope for the request, so the response could not " +
                "be bound to a tenant and project and nothing was kept.");
        }

        if (response is null)
        {
            return Refuse("The source gateway returned no response document.");
        }

        if (response.SchemaVersion != SourceGatewayProtocol.SchemaVersion)
        {
            return Refuse(
                $"The source gateway answered with schema version {response.SchemaVersion}; " +
                $"this build speaks version {SourceGatewayProtocol.SchemaVersion} only.");
        }

        // Correlation before anything else. A response that belongs to another environment, another
        // immutable profile version, or another module is the shape a replay takes, and it must never
        // reach the digest comparison below and be admitted on the strength of matching bytes.
        if (!string.Equals(response.SourceEnvironmentId, request.SourceEnvironmentId, StringComparison.Ordinal) ||
            !string.Equals(response.ExpectedFormsRelease, request.ExpectedFormsRelease, StringComparison.Ordinal) ||
            response.ProfileVersion != request.ProfileVersion ||
            !string.Equals(response.ProfileHash, request.ProfileHash, StringComparison.Ordinal) ||
            !string.Equals(response.ModuleAlias, request.ModuleAlias, StringComparison.Ordinal) ||
            !request.Scope.Matches(response.Scope))
        {
            return Refuse(
                "The source gateway response is not correlated with the request: it names a different source " +
                "environment, Forms release, profile version, profile hash, module, or authorization scope.");
        }

        if (response.ExtractedUtc < serverUtc - SourceGatewayProtocol.MaxClockSkew ||
            response.ExtractedUtc > serverUtc + SourceGatewayProtocol.MaxClockSkew)
        {
            return Refuse("The source gateway extraction timestamp is outside the allowed clock-skew window.");
        }

        if (!SourceGatewayStatus.IsKnown(response.Status))
        {
            return Refuse("The source gateway reported a status this build does not recognize.");
        }

        if (response.Capabilities.Count > SourceGatewayProtocol.MaxCapabilities ||
            response.Findings.Count > SourceGatewayProtocol.MaxFindings)
        {
            return Refuse("The source gateway response declares more capabilities or findings than the protocol allows.");
        }

        foreach (string finding in response.Findings)
        {
            if (finding is null || finding.Length > SourceGatewayProtocol.MaxFindingCharacters)
            {
                return Refuse("A source gateway finding is missing or longer than the protocol allows.");
            }

            if (FleetGuardrails.ContainsPotentialSecret(finding))
            {
                return Refuse("A source gateway finding looked like credential material and the response was refused.");
            }
        }

        if (!string.Equals(response.Status, SourceGatewayStatus.Extracted, StringComparison.Ordinal))
        {
            // A refusal that ships bytes is not a refusal. Treat the whole document as untrustworthy
            // rather than keeping the status and discarding the artifact.
            return response.IntermediateRepresentation is not null
                ? Refuse("The source gateway reported a non-extracted status while still inlining an artifact.")
                : new SourceGatewayAdmission(
                    false,
                    response.Status,
                    null,
                    null,
                    Summarize(response));
        }

        if (!IsSha256(response.ObservedContentSha256) ||
            !string.Equals(response.ObservedContentSha256, request.ExpectedContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse(
                "The source gateway opened a module whose content digest is not the digest this host pinned. " +
                "The two sides are not looking at the same module, so nothing was kept.");
        }

        if (response.ModuleIdentity is not { Length: > 0 and <= 128 } identity ||
            identity.Any(character => char.IsControl(character)))
        {
            return Refuse("The source gateway reported an extracted module with no usable module identity.");
        }

        if (response.IntermediateRepresentation is not { } artifact)
        {
            return Refuse("The source gateway reported an extraction but inlined no intermediate representation.");
        }

        if (!string.Equals(artifact.MediaType, SourceGatewayProtocol.FormsIrMediaType, StringComparison.Ordinal))
        {
            return Refuse("The inlined artifact does not declare the Forms intermediate representation media type.");
        }

        if (!IsSha256(artifact.Sha256))
        {
            return Refuse("The inlined artifact declares no lowercase hexadecimal SHA-256 digest.");
        }

        // Bound the encoded form before decoding, so an oversized body cannot be materialized first and
        // rejected afterwards.
        if (artifact.Base64 is not { Length: > 0 } encoded ||
            encoded.Length > ((SourceGatewayProtocol.MaxArtifactBytes + 2) / 3 * 4) + 4)
        {
            return Refuse("The inlined artifact is empty or larger than the protocol allows.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return Refuse("The inlined artifact is not valid base64.");
        }

        if (bytes.Length is 0 || bytes.Length > SourceGatewayProtocol.MaxArtifactBytes)
        {
            return Refuse("The decoded artifact is empty or larger than the protocol allows.");
        }

        string observed = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(observed, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse("The decoded artifact does not hash to the digest the source gateway declared.");
        }

        if (InspectExtractedIr(bytes, request) is { } irError)
        {
            return Refuse(irError);
        }

        return new SourceGatewayAdmission(true, SourceGatewayStatus.Extracted, bytes, identity, Summarize(response));
    }

    /// <summary>
    /// Reads the artifact as the untrusted document it is: field by field, refusing anything that is not
    /// the native worker's own extraction output for exactly this request.
    ///
    /// The generator check is the load-bearing one. The host's intermediate reader admits only documents
    /// carrying <c>oracle-forms-migration-fleet/source-normalization</c> plus <c>normalized: true</c>, and
    /// those are written by the normalization phase after it adjudicates an estate against a session
    /// source root. A gateway that could emit them would have skipped that phase entirely, so an artifact
    /// declaring either is refused here rather than stored and discovered later.
    /// </summary>
    private static string? InspectExtractedIr(byte[] bytes, SourceGatewayFormsModuleRequest request)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return "The inlined intermediate representation is not valid JSON.";
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return "The inlined intermediate representation is not a JSON object.";
            }

            foreach (string field in SourceGatewayProtocol.AdjudicationFields)
            {
                if (root.TryGetProperty(field, out _))
                {
                    return
                        $"The inlined intermediate representation declares '{field}', which only this host's " +
                        "normalization phase may write. Extraction output that claims adjudication was refused.";
                }
            }

            if (Text(root, "generator") is not { } generator ||
                !string.Equals(generator, SourceGatewayProtocol.ExtractedIrGenerator, StringComparison.Ordinal))
            {
                return
                    "The inlined intermediate representation was not written by the native source worker, so " +
                    "nothing is known to carry its field meanings.";
            }

            string? schemaVersion = Text(root, "schemaVersion");
            if (!string.Equals(schemaVersion, SourceGatewayProtocol.ExtractedIrSchemaVersion, StringComparison.Ordinal))
            {
                return
                    $"The inlined intermediate representation declares body version " +
                    $"'{schemaVersion ?? "(absent)"}'; this build admits version " +
                    $"'{SourceGatewayProtocol.ExtractedIrSchemaVersion}' only.";
            }

            if (!string.Equals(Text(root, "sourceEnvironmentId"), request.SourceEnvironmentId, StringComparison.Ordinal) ||
                !string.Equals(Text(root, "moduleAlias"), request.ModuleAlias, StringComparison.Ordinal) ||
                !string.Equals(Text(root, "profileHash"), request.ProfileHash, StringComparison.Ordinal) ||
                !string.Equals(Text(root, "contentSha256"), request.ExpectedContentSha256, StringComparison.OrdinalIgnoreCase))
            {
                return "The inlined intermediate representation describes a different module, profile, or content digest.";
            }

            if (!root.TryGetProperty("profileVersion", out JsonElement version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out int declaredVersion) ||
                declaredVersion != request.ProfileVersion)
            {
                return "The inlined intermediate representation records a different source profile version.";
            }

            if (!root.TryGetProperty("modules", out JsonElement modules) ||
                modules.ValueKind != JsonValueKind.Array ||
                modules.GetArrayLength() == 0)
            {
                return "The inlined intermediate representation carries no extracted module.";
            }
        }

        return null;
    }

    /// <summary>
    /// Decides whether an Oracle schema response may be believed, and hands back only bytes it verified
    /// itself plus the statements it read out of them.
    ///
    /// The rules mirror the Forms half deliberately. What is specific here is the allowlist check: the
    /// response must cover exactly the schemas the request named, in the same order, because a gateway
    /// that quietly returned a different set would produce DDL for something nobody approved reading.
    /// </summary>
    public static SourceGatewaySchemaAdmission AdmitOracleSchema(
        SourceGatewayOracleSchemaRequest request,
        SourceGatewayOracleSchemaResponse? response,
        DateTimeOffset serverUtc)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!SourceGatewayAuthorizationScope.IsWellFormed(request.Scope))
        {
            return RefuseSchema(
                "This host did not derive a server-owned authorization scope for the request, so the response could not " +
                "be bound to a tenant and project and nothing was kept.");
        }

        if (response is null)
        {
            return RefuseSchema("The source gateway returned no response document.");
        }

        if (response.SchemaVersion != SourceGatewayProtocol.SchemaVersion)
        {
            return RefuseSchema(
                $"The source gateway answered with schema version {response.SchemaVersion}; " +
                $"this build speaks version {SourceGatewayProtocol.SchemaVersion} only.");
        }

        if (!string.Equals(response.SourceEnvironmentId, request.SourceEnvironmentId, StringComparison.Ordinal) ||
            response.ProfileVersion != request.ProfileVersion ||
            !string.Equals(response.ProfileHash, request.ProfileHash, StringComparison.Ordinal) ||
            !request.Scope.Matches(response.Scope))
        {
            return RefuseSchema(
                "The source gateway response is not correlated with the request: it names a different source " +
                "environment, profile version, profile hash, or authorization scope.");
        }

        if (response.ExtractedUtc < serverUtc - SourceGatewayProtocol.MaxClockSkew ||
            response.ExtractedUtc > serverUtc + SourceGatewayProtocol.MaxClockSkew)
        {
            return RefuseSchema("The source gateway extraction timestamp is outside the allowed clock-skew window.");
        }

        if (!SourceGatewayStatus.IsKnown(response.Status))
        {
            return RefuseSchema("The source gateway reported a status this build does not recognize.");
        }

        if (response.Capabilities.Count > SourceGatewayProtocol.MaxCapabilities ||
            response.Findings.Count > SourceGatewayProtocol.MaxFindings)
        {
            return RefuseSchema("The source gateway response declares more capabilities or findings than the protocol allows.");
        }

        foreach (string finding in response.Findings)
        {
            if (finding is null || finding.Length > SourceGatewayProtocol.MaxFindingCharacters)
            {
                return RefuseSchema("A source gateway finding is missing or longer than the protocol allows.");
            }

            if (FleetGuardrails.ContainsPotentialSecret(finding))
            {
                return RefuseSchema("A source gateway finding looked like credential material and the response was refused.");
            }
        }

        if (!string.Equals(response.Status, SourceGatewayStatus.Extracted, StringComparison.Ordinal))
        {
            return response.SchemaArtifact is not null
                ? RefuseSchema("The source gateway reported a non-extracted status while still inlining an artifact.")
                : new SourceGatewaySchemaAdmission(false, response.Status, null, null, null, SummarizeSchema(response));
        }

        if (response.SchemaArtifact is not { } artifact)
        {
            return RefuseSchema("The source gateway reported an extraction but inlined no schema artifact.");
        }

        if (!string.Equals(artifact.MediaType, SourceGatewayProtocol.OracleSchemaMediaType, StringComparison.Ordinal))
        {
            return RefuseSchema("The inlined artifact does not declare the Oracle schema media type.");
        }

        if (!IsSha256(artifact.Sha256))
        {
            return RefuseSchema("The inlined artifact declares no lowercase hexadecimal SHA-256 digest.");
        }

        if (artifact.Base64 is not { Length: > 0 } encoded ||
            encoded.Length > ((SourceGatewayProtocol.MaxArtifactBytes + 2) / 3 * 4) + 4)
        {
            return RefuseSchema("The inlined artifact is empty or larger than the protocol allows.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return RefuseSchema("The inlined artifact is not valid base64.");
        }

        if (bytes.Length is 0 || bytes.Length > SourceGatewayProtocol.MaxArtifactBytes)
        {
            return RefuseSchema("The decoded artifact is empty or larger than the protocol allows.");
        }

        string observed = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(observed, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return RefuseSchema("The decoded artifact does not hash to the digest the source gateway declared.");
        }

        // The snapshot digest is what the run record pins, so a response whose own digest disagrees with
        // the artifact it shipped is refused rather than recorded against the wrong identity.
        if (!IsSha256(response.SnapshotHash) ||
            !string.Equals(response.SnapshotHash, observed, StringComparison.OrdinalIgnoreCase))
        {
            return RefuseSchema("The source gateway declared a snapshot digest that is not the digest of the artifact it returned.");
        }

        (SourceGatewaySchemaProjection? projection, string? error) = ReadExtractedSchema(bytes, request);
        return projection is null
            ? RefuseSchema(error!)
            : new SourceGatewaySchemaAdmission(true, SourceGatewayStatus.Extracted, bytes, observed, projection, SummarizeSchema(response));
    }

    /// <summary>
    /// Reads the schema artifact as the untrusted document it is, then splits the canonical DDL into the
    /// two evidence kinds this host's indexer recognizes.
    ///
    /// The split is located, never inferred: the worker's emitter writes
    /// <see cref="SourceGatewayProtocol.ProgramUnitSectionMarker"/> at the start of a line before each
    /// schema's program units, and an artifact that reports program units without that boundary is
    /// refused. Emitting the whole document as one file instead would be the quiet option and would
    /// misclassify the PL/SQL the conversion phase needs.
    /// </summary>
    private static (SourceGatewaySchemaProjection? Projection, string? Error) ReadExtractedSchema(
        byte[] bytes,
        SourceGatewayOracleSchemaRequest request)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return (null, "The inlined schema artifact is not valid JSON.");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, "The inlined schema artifact is not a JSON object.");
            }

            foreach (string field in SourceGatewayProtocol.AdjudicationFields)
            {
                if (root.TryGetProperty(field, out _))
                {
                    return (null,
                        $"The inlined schema artifact declares '{field}', which only this host's normalization phase " +
                        "may write. Extraction output that claims adjudication was refused.");
                }
            }

            if (!string.Equals(Text(root, "generator"), SourceGatewayProtocol.ExtractedSchemaGenerator, StringComparison.Ordinal))
            {
                return (null,
                    "The inlined schema artifact was not written by the source worker's Oracle catalog reader, so " +
                    "nothing is known to carry its field meanings.");
            }

            if (!string.Equals(Text(root, "schemaVersion"), SourceGatewayProtocol.ExtractedSchemaBodyVersion, StringComparison.Ordinal))
            {
                return (null,
                    $"The inlined schema artifact declares body version '{Text(root, "schemaVersion") ?? "(absent)"}'; " +
                    $"this build admits version '{SourceGatewayProtocol.ExtractedSchemaBodyVersion}' only.");
            }

            if (!string.Equals(Text(root, "sourceEnvironmentId"), request.SourceEnvironmentId, StringComparison.Ordinal) ||
                !string.Equals(Text(root, "profileHash"), request.ProfileHash, StringComparison.Ordinal))
            {
                return (null, "The inlined schema artifact describes a different source environment or profile.");
            }

            if (!root.TryGetProperty("profileVersion", out JsonElement version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out int declaredVersion) ||
                declaredVersion != request.ProfileVersion)
            {
                return (null, "The inlined schema artifact records a different source profile version.");
            }

            if (ReadSchemas(root) is not { } schemas)
            {
                return (null, "The inlined schema artifact does not name the schemas it covers.");
            }

            if (!schemas.SequenceEqual(request.SchemaAllowlist, StringComparer.Ordinal))
            {
                return (null,
                    "The inlined schema artifact covers a different set of schemas than the request allowed, so it " +
                    "was refused rather than stored.");
            }

            if (!root.TryGetProperty("coverage", out JsonElement coverage) || coverage.ValueKind != JsonValueKind.Object)
            {
                return (null, "The inlined schema artifact reports no coverage counts.");
            }

            int tables = Count(coverage, "tables");
            int columns = Count(coverage, "columns");
            int constraints = Count(coverage, "constraints");
            int sequences = Count(coverage, "sequences");
            int programUnits = Count(coverage, "programUnits");
            int objects = Count(coverage, "objects");
            int indexes = Count(coverage, "indexes");
            int grants = Count(coverage, "grants");

            if (tables < 0 || columns < 0 || constraints < 0 || sequences < 0 || programUnits < 0 ||
                objects < 0 || indexes < 0 || grants < 0)
            {
                return (null, "The inlined schema artifact reports coverage counts this build cannot read.");
            }

            if (Math.Max(Math.Max(Math.Max(tables, columns), Math.Max(constraints, sequences)),
                    Math.Max(Math.Max(programUnits, objects), Math.Max(indexes, grants))) >
                SourceGatewayProtocol.MaxSchemaObjects)
            {
                return (null,
                    $"The inlined schema artifact reports a coverage count over this build's " +
                    $"{SourceGatewayProtocol.MaxSchemaObjects} ceiling, so it was refused before anything was counted.");
            }

            if (Text(root, "ddl") is not { Length: > 0 } ddl)
            {
                return (null, "The inlined schema artifact carries no DDL, so there is nothing this host could store.");
            }

            string provider = Text(root, "provider") is { Length: > 0 and <= 64 } alias ? alias : "unspecified";

            int boundary = FindProgramUnitBoundary(ddl);
            if (programUnits > 0 && boundary < 0)
            {
                return (null,
                    "The inlined schema artifact reports program units but carries no program-unit section boundary, " +
                    "so the PL/SQL could not be separated from the schema DDL and nothing was stored.");
            }

            if (programUnits == 0 && boundary >= 0)
            {
                return (null,
                    "The inlined schema artifact carries a program-unit section while reporting no program unit, so " +
                    "its own coverage does not describe it and nothing was stored.");
            }

            string schemaDdl = boundary < 0 ? ddl : ddl[..boundary];
            string? programUnitSql = boundary < 0 ? null : ddl[boundary..];

            if (schemaDdl.TrimEnd().Length == 0 && (tables > 0 || sequences > 0))
            {
                return (null, "The inlined schema artifact reports tables or sequences but emitted no schema DDL before its program units.");
            }

            // Counts and a located boundary only establish that the document is shaped like an extraction.
            // What it actually carries is read here, statement by statement, and reconciled against the
            // inventory it declares; a digest proves the gateway meant to send these bytes, never that the
            // bytes describe the schema they claim to.
            if (SchemaArtifactConsistency.Reconcile(
                    root,
                    schemas,
                    new SchemaArtifactCoverage(objects, tables, columns, constraints, sequences, indexes, grants, programUnits),
                    schemaDdl,
                    programUnitSql) is { } inconsistency)
            {
                return (null, inconsistency);
            }

            return (new SourceGatewaySchemaProjection(
                schemas, provider, schemaDdl, programUnitSql, tables, columns, constraints, sequences, programUnits), null);
        }
    }

    /// <summary>Index of the first line that opens the program-unit section, or -1 when there is none.</summary>
    private static int FindProgramUnitBoundary(string ddl)
    {
        int index = 0;
        while (index < ddl.Length)
        {
            int end = ddl.IndexOf('\n', index);
            int length = (end < 0 ? ddl.Length : end) - index;
            if (string.CompareOrdinal(
                    ddl, index, SourceGatewayProtocol.ProgramUnitSectionMarker, 0,
                    Math.Min(length, SourceGatewayProtocol.ProgramUnitSectionMarker.Length)) == 0 &&
                length >= SourceGatewayProtocol.ProgramUnitSectionMarker.Length)
            {
                return index;
            }

            if (end < 0)
            {
                return -1;
            }

            index = end + 1;
        }

        return -1;
    }

    private static IReadOnlyList<string>? ReadSchemas(JsonElement root)
    {
        if (!root.TryGetProperty("schemas", out JsonElement schemas) ||
            schemas.ValueKind != JsonValueKind.Array ||
            schemas.GetArrayLength() is 0 or > SourceGatewayProtocol.MaxSchemas)
        {
            return null;
        }

        List<string> names = [];
        foreach (JsonElement schema in schemas.EnumerateArray())
        {
            if (schema.ValueKind != JsonValueKind.String || schema.GetString() is not { Length: > 0 and <= 30 } name)
            {
                return null;
            }

            names.Add(name);
        }

        return names;
    }

    private static int Count(JsonElement coverage, string name) =>
        coverage.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int parsed) && parsed >= 0
            ? parsed
            : -1;

    private static string SummarizeSchema(SourceGatewayOracleSchemaResponse response) =>
        response.Findings.Count > 0
            ? string.Join(" ", response.Findings)
            : $"The source gateway reported {response.Status} and stated no finding.";

    private static SourceGatewaySchemaAdmission RefuseSchema(string reason) =>
        new(false, SourceGatewayStatus.Rejected, null, null, null, reason);

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static string Summarize(SourceGatewayFormsModuleResponse response) =>
        response.Findings.Count > 0
            ? string.Join(" ", response.Findings)
            : $"The source gateway reported {response.Status} and stated no finding.";

    private static SourceGatewayAdmission Refuse(string reason) =>
        new(false, SourceGatewayStatus.Rejected, null, null, reason);
}
