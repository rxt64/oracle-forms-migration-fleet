// Copyright (c) Microsoft. All rights reserved.

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OracleFormsMigrationFleet.Fleet;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>An organization, keyed by the Entra tenant its members sign in from.</summary>
public sealed record PlatformOrganization
{
    public required string OrganizationId { get; init; }

    public required string TenantId { get; init; }

    public required string DisplayName { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public int Version { get; init; } = 1;
}

/// <summary>A unit of work members are granted access to. Nothing crosses a project boundary.</summary>
public sealed record PlatformProject
{
    public required string ProjectId { get; init; }

    public required string OrganizationId { get; init; }

    public required string TenantId { get; init; }

    public required string Name { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public int Version { get; init; } = 1;
}

/// <summary>
/// One actor's standing in one project.
///
/// Removal is recorded rather than deleted so an audit can show when authority ended, and a membership
/// with <see cref="RemovedUtc"/> set authorizes nothing.
/// </summary>
public sealed record PlatformMembership
{
    public required string ProjectId { get; init; }

    public required string TenantId { get; init; }

    public required string ObjectId { get; init; }

    public required IReadOnlyList<string> Roles { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset? RemovedUtc { get; init; }

    public int Version { get; init; } = 1;

    public bool IsActive => RemovedUtc is null;

    public bool HasRole(string role) =>
        IsActive && Roles.Any(held => string.Equals(held, role, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The identity of a migration target, owned by the server and immutable once written.
///
/// Every field here is a non-secret coordinate. There is no password, connection string, or caller URL,
/// because a target a caller can describe is a target a caller can redirect. A change produces a new
/// <see cref="Version"/> rather than an edit, so a grant issued against version 3 stops authorizing the
/// moment version 4 exists.
/// </summary>
public sealed record PlatformTargetProfile
{
    public required string TargetProfileId { get; init; }

    public required string ProjectId { get; init; }

    public required string TenantId { get; init; }

    public required int Version { get; init; }

    /// <summary>Azure tenant the target resource lives in. Not necessarily the sign-in tenant.</summary>
    public required string AzureTenantId { get; init; }

    public required string SubscriptionId { get; init; }

    public required string ResourceGroup { get; init; }

    public required string ResourceId { get; init; }

    public required string Region { get; init; }

    /// <summary>Host name only. No scheme, no port, no user information.</summary>
    public required string EndpointHost { get; init; }

    public required string DatabaseName { get; init; }

    public required string SchemaName { get; init; }

    /// <summary>The identity the workbench authenticates to the target as. A name, never a credential.</summary>
    public required string ExecutionIdentity { get; init; }

    /// <summary>Sandbox, staging, production. A label the approval flow reads, not free text for the run.</summary>
    public required string EnvironmentName { get; init; }

    public required string StackDatabase { get; init; }

    public required string StackFrontEnd { get; init; }

    public required string StackBackEnd { get; init; }

    public required string CanonicalHash { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }
}

/// <summary>Where an approval request is in its life. Every transition is recorded with who and when.</summary>
public enum PlatformApprovalState
{
    Requested,
    Approved,
    Rejected,
    Revoked,
}

/// <summary>
/// One approval request and everything that has happened to it.
///
/// The bindings are derived by the server from the owned workspace, the sanitized plan input, and the
/// persisted target profile. A caller supplies a project, a profile identifier, a scope, and a note;
/// every hash, actor, role, and timestamp on this record was written by the server.
/// </summary>
public sealed record PlatformApproval
{
    public required string ApprovalId { get; init; }

    public required string ProjectId { get; init; }

    public required string TenantId { get; init; }

    public required string RequestedByObjectId { get; init; }

    public required DateTimeOffset RequestedUtc { get; init; }

    public required PlatformApprovalState State { get; init; }

    public required WorkbenchMutationScope Scope { get; init; }

    public required string RequiredRole { get; init; }

    public required string EngagementId { get; init; }

    public required string SourceSnapshotHash { get; init; }

    public required string PlanInputHash { get; init; }

    public required string TargetProfileId { get; init; }

    public required int TargetProfileVersion { get; init; }

    public required string TargetProfileHash { get; init; }

    public required DateTimeOffset ExpiresUtc { get; init; }

    public string? RequestNotes { get; init; }

    public string? DecidedByObjectId { get; init; }

    public DateTimeOffset? DecidedUtc { get; init; }

    public string? DecisionNotes { get; init; }

    public string? RevokedByObjectId { get; init; }

    public DateTimeOffset? RevokedUtc { get; init; }

    public string? RevocationNotes { get; init; }

    public int Version { get; init; } = 1;

    public bool IsEffective(DateTimeOffset nowUtc) =>
        State == PlatformApprovalState.Approved && RevokedUtc is null && ExpiresUtc > nowUtc;
}

/// <summary>Why a target profile was refused. The reason is shown; the rejected value never is.</summary>
public sealed record PlatformTargetProfileRejection(string Reason);

/// <summary>
/// Validation and canonical identity for target profiles.
///
/// The hash covers the immutable version, Azure coordinates, endpoint identity, and stack selection so
/// an approval cannot be replayed against a different destination or generated application shape.
/// </summary>
public static class PlatformTargetProfiles
{
    private static readonly JsonSerializerOptions s_canonical = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    /// <summary>
    /// Rejects anything that is not a plain non-secret coordinate.
    ///
    /// The endpoint is a host name, so a caller cannot smuggle a credential through user information in
    /// a URI, and the whole record is passed through the same intake guard the fleet uses on evidence,
    /// so a value that looks like credential material never reaches the store or a log line.
    /// </summary>
    public static bool TryValidate(
        PlatformTargetProfile profile,
        [NotNullWhen(false)] out PlatformTargetProfileRejection? rejection)
    {
        ArgumentNullException.ThrowIfNull(profile);

        string[] required =
        [
            profile.AzureTenantId, profile.SubscriptionId, profile.ResourceGroup, profile.ResourceId,
            profile.Region, profile.EndpointHost, profile.DatabaseName, profile.SchemaName,
            profile.ExecutionIdentity, profile.EnvironmentName,
        ];

        if (required.Any(string.IsNullOrWhiteSpace))
        {
            rejection = new PlatformTargetProfileRejection("A target profile must name every Azure coordinate it binds.");
            return false;
        }

        if (profile.Version < 1)
        {
            rejection = new PlatformTargetProfileRejection("A target profile version starts at 1.");
            return false;
        }

        if (profile.EndpointHost.Contains("://", StringComparison.Ordinal) ||
            profile.EndpointHost.Contains('@', StringComparison.Ordinal) ||
            profile.EndpointHost.Contains('/', StringComparison.Ordinal) ||
            profile.EndpointHost.Contains(':', StringComparison.Ordinal))
        {
            rejection = new PlatformTargetProfileRejection(
                "The endpoint must be a host name. A URI could carry a credential or redirect the target.");
            return false;
        }

        if (Uri.CheckHostName(profile.EndpointHost) == UriHostNameType.Unknown)
        {
            rejection = new PlatformTargetProfileRejection("The endpoint host name is not a host name.");
            return false;
        }

        foreach (string value in required)
        {
            if (FleetGuardrails.ContainsPotentialSecret(value))
            {
                rejection = new PlatformTargetProfileRejection(
                    "A target profile field looked like credential material and was rejected before it was stored.");
                return false;
            }
        }

        rejection = null;
        return true;
    }

    /// <summary>The non-secret canonical digest a grant is bound to.</summary>
    public static string Hash(PlatformTargetProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        Canonical canonical = new(
            profile.TargetProfileId,
            profile.ProjectId,
            profile.Version,
            profile.AzureTenantId.ToLowerInvariant(),
            profile.SubscriptionId.ToLowerInvariant(),
            profile.ResourceGroup.ToLowerInvariant(),
            profile.ResourceId.ToLowerInvariant(),
            profile.Region.ToLowerInvariant(),
            profile.EndpointHost.ToLowerInvariant(),
            profile.DatabaseName,
            profile.SchemaName,
            profile.ExecutionIdentity,
            profile.EnvironmentName,
            profile.StackDatabase,
            profile.StackFrontEnd,
            profile.StackBackEnd);

        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical, s_canonical)))
            .ToLowerInvariant();
    }

    private sealed record Canonical(
        string TargetProfileId,
        string ProjectId,
        int Version,
        string AzureTenantId,
        string SubscriptionId,
        string ResourceGroup,
        string ResourceId,
        string Region,
        string EndpointHost,
        string DatabaseName,
        string SchemaName,
        string ExecutionIdentity,
        string EnvironmentName,
        string StackDatabase,
        string StackFrontEnd,
        string StackBackEnd);
}

/// <summary>
/// The persisted platform state this workbench authorizes against.
///
/// Every method is asynchronous because the production implementation is a database and the request
/// path must not block a thread on it. Concurrency is optimistic: a caller passes the version it read,
/// and a transition that lost the race returns null rather than overwriting someone else's decision.
/// </summary>
public interface IPlatformStateStore
{
    /// <summary>Human-readable name of the backing store, for the console and for logs. Never a connection string.</summary>
    string Description { get; }

    Task InitializeAsync(CancellationToken cancellationToken);

    Task<PlatformOrganization> EnsureOrganizationAsync(string tenantId, string displayName, CancellationToken cancellationToken);

    Task<PlatformProject> CreateProjectAsync(PlatformProject project, PlatformMembership founder, CancellationToken cancellationToken);

    Task<PlatformProject?> GetProjectAsync(string tenantId, string projectId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PlatformProject>> ProjectsForActorAsync(string tenantId, string objectId, CancellationToken cancellationToken);

    Task<PlatformMembership?> GetMembershipAsync(string tenantId, string projectId, string objectId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PlatformMembership>> MembershipsAsync(string tenantId, string projectId, CancellationToken cancellationToken);

    Task<PlatformMembership?> UpsertMembershipAsync(PlatformMembership membership, int? expectedVersion, CancellationToken cancellationToken);

    Task<PlatformTargetProfile?> CreateTargetProfileAsync(PlatformTargetProfile profile, CancellationToken cancellationToken);

    Task<PlatformTargetProfile?> GetTargetProfileAsync(string tenantId, string projectId, string targetProfileId, int? version, CancellationToken cancellationToken);

    Task<IReadOnlyList<PlatformTargetProfile>> TargetProfilesAsync(string tenantId, string projectId, CancellationToken cancellationToken);

    Task<PlatformApproval> CreateApprovalAsync(PlatformApproval approval, CancellationToken cancellationToken);

    Task<PlatformApproval?> GetApprovalAsync(string tenantId, string approvalId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PlatformApproval>> ApprovalsForProjectAsync(string tenantId, string projectId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PlatformApproval>> ApprovalsForRequesterAsync(string tenantId, string objectId, CancellationToken cancellationToken);

    /// <summary>Returns null when the stored version is not the one the caller read.</summary>
    Task<PlatformApproval?> UpdateApprovalAsync(PlatformApproval approval, int expectedVersion, CancellationToken cancellationToken);
}
