using System.Text.Json;

namespace OracleFormsMigrationFleet.SourceWorker.Gateway;

/// <summary>
/// The server half of the source gateway wire contract. The web host owns the calling half in
/// <c>Fleet/Execution/SourceGatewayProtocol.cs</c>; this type restates the same shapes so the gateway can
/// be deployed, built and tested without referencing the web host.
///
/// Two invariants are load bearing and are the reason the shapes are not simply the worker's own:
///
/// 1. A response carries artifact BYTES. The native worker writes its intermediate representation to this
///    machine's disk; that path means nothing to the caller and honouring it would be a remote-controlled
///    read on the caller. The gateway therefore validates the path against its own configured output root,
///    hashes the file itself, and inlines the bytes. No response field ever carries a filesystem location.
/// 2. Nothing here adjudicates. The gateway reports what the worker observed. It issues no attestation,
///    no approval and no normalization claim, and it never writes the host's normalization fields.
/// </summary>
public static class GatewayProtocol
{
    public const int SchemaVersion = 1;

    public const string FormsModuleExtractPath = "/source/forms-module/extract";

    public const string OracleSchemaExtractPath = "/source/oracle-schema/extract";

    public const string FormsIrMediaType = "application/vnd.oracle-forms-migration-fleet.forms-ir+json";

    public const string OracleSchemaMediaType = "application/vnd.oracle-forms-migration-fleet.oracle-schema+json";

    /// <summary>A request carries identifiers and digests only, so it is capped well below a page of text.</summary>
    public const int MaxRequestBytes = 8 * 1024;

    public const int MaxArtifactBytes = 16 * 1024 * 1024;

    public const int MaxFindings = 64;

    public const int MaxFindingCharacters = 512;

    public const int MaxCapabilities = 32;

    public const int MaxSchemaAllowlistEntries = 64;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
}

/// <summary>
/// Who the calling workbench says the request is for, as ITS server decided: a tenant and a project.
///
/// This exists because the bearer token cannot answer the question. The workbench calls with one shared
/// application identity, so every project it hosts presents the same <c>appid</c> and the same <c>tid</c>.
/// Without a scope this gateway would have no way to tell a request made on behalf of the project an
/// operator registered a source environment for from one made on behalf of any other project in the same
/// tenant — and would serve both, which is the confused deputy this field closes.
///
/// The gateway does not believe the scope on its own. <see cref="TenantId"/> must equal the <c>tid</c> in
/// the validated token, and both parts must match the binding on the registry entry the request names.
/// The project part is asserted by the calling server and is trusted only as far as that registry entry
/// allows: an operator lists which projects a source environment may be read for, so a compromised
/// workbench can still only reach the projects that were approved for it.
/// </summary>
public sealed record GatewayAuthorizationScope(string TenantId, string ProjectId);

/// <summary>The caller as the validated token describes it. Never read from a header or a request body.</summary>
public sealed record GatewayCallerIdentity(string TenantId, string ApplicationId);

/// <summary>Terminal states the gateway is willing to report. The caller refuses anything else.</summary>
public static class GatewayStatus
{
    /// <summary>The module was opened and an artifact is inlined. The only status that carries bytes.</summary>
    public const string Extracted = "Extracted";

    /// <summary>A prerequisite is unmet on this gateway. No module was opened.</summary>
    public const string BlockedPrerequisite = "BlockedPrerequisite";

    /// <summary>The request was refused before any work started.</summary>
    public const string Rejected = "Rejected";

    /// <summary>Work started and failed. No artifact.</summary>
    public const string ExtractionFailed = "ExtractionFailed";

    public static bool IsKnown(string? status) =>
        status is Extracted or BlockedPrerequisite or Rejected or ExtractionFailed;
}

/// <summary><paramref name="Sha256"/> is the digest of the DECODED bytes, recomputed by the caller.</summary>
public sealed record GatewayInlineArtifact(string MediaType, string Sha256, string Base64);

public sealed record GatewayCapability(
    string Id,
    string State,
    string Prerequisite,
    string? RequiredRelease,
    string? RequiredArchitecture,
    string? RequiredHost,
    string Remediation,
    string? Observed);

public sealed record GatewayNativeEvidence(
    string LibraryAlias,
    string? FileVersion,
    string? ProductVersion,
    string LibrarySha256,
    string DefinitionHeaderSha256,
    IReadOnlyList<string> ResolvedExports,
    IReadOnlyList<string> ResolvedConstants);

/// <summary>
/// A request for one module that the operator already placed under this gateway's configured input root.
/// There is no path, command, library name or connect string: the caller names an alias and pins a digest,
/// and every location comes from this server's own registry.
/// </summary>
public sealed record GatewayFormsModuleRequest(
    int SchemaVersion,
    string SourceEnvironmentId,
    string ExpectedFormsRelease,
    int ProfileVersion,
    string ProfileHash,
    string ModuleAlias,
    string ExpectedContentSha256,
    GatewayAuthorizationScope? Scope = null);

/// <summary>
/// Every correlation field is echoed, so a response cannot be replayed against another source environment,
/// profile version, or module.
/// </summary>
public sealed record GatewayFormsModuleResponse(
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
    GatewayInlineArtifact? IntermediateRepresentation,
    GatewayNativeEvidence? Native,
    IReadOnlyList<GatewayCapability> Capabilities,
    IReadOnlyList<string> Findings,
    GatewayAuthorizationScope? Scope = null);

/// <summary>
/// A request for the Oracle schema of the schemas allowlisted on the registered source environment.
/// The caller's list is intersected with the registry; it can never widen it, and no credential travels.
/// </summary>
public sealed record GatewayOracleSchemaRequest(
    int SchemaVersion,
    string SourceEnvironmentId,
    int ProfileVersion,
    string ProfileHash,
    IReadOnlyList<string> SchemaAllowlist,
    GatewayAuthorizationScope? Scope = null);

public sealed record GatewayOracleSchemaResponse(
    int SchemaVersion,
    string Status,
    string SourceEnvironmentId,
    int ProfileVersion,
    string ProfileHash,
    DateTimeOffset ExtractedUtc,
    string? SnapshotHash,
    GatewayInlineArtifact? SchemaArtifact,
    IReadOnlyList<GatewayCapability> Capabilities,
    IReadOnlyList<string> Findings,
    GatewayAuthorizationScope? Scope = null);
