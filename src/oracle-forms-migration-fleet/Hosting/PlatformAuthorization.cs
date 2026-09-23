// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>
/// The identity of the sandbox database this process is actually configured to write to.
///
/// It exists so a persisted target profile can be compared against reality before a grant is honoured.
/// Everything on it is a non-secret coordinate the browser may see; the credential the gateway uses is
/// not represented here and never leaves the process.
/// </summary>
public interface ISandboxTargetBinding
{
    string EndpointHost { get; }

    string DatabaseName { get; }

    string ExecutionIdentity { get; }

    /// <summary>True when a data migration gateway backed by this binding is registered.</summary>
    bool CanWrite { get; }
}

/// <summary>A binding read from host configuration. The caller never supplies any part of it.</summary>
public sealed record ConfiguredSandboxTargetBinding(
    string EndpointHost,
    string DatabaseName,
    string ExecutionIdentity,
    bool CanWrite) : ISandboxTargetBinding;

/// <summary>Outcome of a platform operation, shaped so an endpoint can answer without exceptions.</summary>
public sealed record PlatformResult<T>(bool Succeeded, T? Value, int Status, string Error)
{
    public static PlatformResult<T> Ok(T value) => new(true, value, 200, string.Empty);

    public static PlatformResult<T> Fail(int status, string error) => new(false, default, status, error);
}

/// <summary>
/// What a caller asks for when requesting an approval. Every binding is derived, not supplied.
///
/// <paramref name="RequestedTarget"/> is the stack the server read back out of the sanitized run
/// request. It is carried here so the approval can be refused when it names a destination the project's
/// immutable target profile does not, rather than being bound into a grant that later authorizes it.
/// </summary>
public sealed record PlatformApprovalRequestInput(
    string ProjectId,
    string TargetProfileId,
    WorkbenchMutationScope Scope,
    string EngagementId,
    string SourceSnapshotHash,
    string PlanInputHash,
    TimeSpan Lifetime,
    string? Notes,
    TargetStack? RequestedTarget);

/// <summary>
/// Projects persisted approvals into the grants the trust boundary understands.
///
/// A grant is not stored. It is computed from an approval that is currently approved, unexpired, and
/// unrevoked, whose target profile still exists at the version it was approved against, and whose target
/// profile still matches the sandbox this process is actually wired to. Any of those ceasing to be true
/// makes the grant disappear at the next check, which is what makes revocation and drift effective
/// without a second store to keep in step.
/// </summary>
public sealed class PlatformAuthorizationStore(
    IPlatformStateStore store,
    ISandboxTargetBinding? sandbox,
    Func<DateTimeOffset>? clock = null,
    ISandboxProjectBindingStore? sandboxProjects = null) : IWorkbenchAuthorizationStore
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly ISandboxProjectBindingStore? _sandboxProjects =
        sandboxProjects ?? store as ISandboxProjectBindingStore;

    public async Task<IReadOnlyList<WorkbenchAuthorizationRecord>> ForOwnerAsync(
        string ownerId,
        CancellationToken cancellationToken)
    {
        if (!PlatformIdentity.TrySplitOwner(ownerId, out string tenantId, out string objectId))
        {
            return [];
        }

        DateTimeOffset now = _clock();
        IReadOnlyList<PlatformApproval> approvals =
            await store.ApprovalsForRequesterAsync(tenantId, objectId, cancellationToken).ConfigureAwait(false);
        string? sandboxProject = _sandboxProjects is null
            ? null
            : await _sandboxProjects.GetSandboxProjectAsync(tenantId, cancellationToken).ConfigureAwait(false);

        List<WorkbenchAuthorizationRecord> grants = [];
        foreach (PlatformApproval approval in approvals)
        {
            if (!approval.IsEffective(now) || approval.Scope != WorkbenchMutationScope.SandboxDatabaseWrite)
            {
                continue;
            }

            if (!string.Equals(sandboxProject, approval.ProjectId, StringComparison.Ordinal))
            {
                continue;
            }

            PlatformMembership? membership = await store
                .GetMembershipAsync(tenantId, approval.ProjectId, objectId, cancellationToken)
                .ConfigureAwait(false);

            // Membership removed after approval revokes the grant without anyone touching the approval.
            if (membership is null || !membership.HasRole(approval.RequiredRole))
            {
                continue;
            }

            PlatformTargetProfile? profile = await store
                .GetTargetProfileAsync(tenantId, approval.ProjectId, approval.TargetProfileId, version: null, cancellationToken)
                .ConfigureAwait(false);

            if (profile is null ||
                profile.Version != approval.TargetProfileVersion ||
                !string.Equals(profile.CanonicalHash, approval.TargetProfileHash, StringComparison.Ordinal) ||
                !MatchesConfiguredSandbox(profile))
            {
                continue;
            }

            grants.Add(new WorkbenchAuthorizationRecord
            {
                AuthorizationId = approval.ApprovalId,
                OwnerId = ownerId,
                RequiredRole = approval.RequiredRole,
                EngagementId = approval.EngagementId,
                SourceSnapshotHash = approval.SourceSnapshotHash,
                PlanInputHash = approval.PlanInputHash,
                TargetHash = approval.TargetProfileHash,
                Scope = approval.Scope,
                ExpiresUtc = approval.ExpiresUtc,
                TenantId = tenantId,
                ProjectId = approval.ProjectId,
                TargetProfileId = approval.TargetProfileId,
                TargetProfileVersion = approval.TargetProfileVersion,
                ApprovedByObjectId = approval.DecidedByObjectId ?? string.Empty,
                IsRevoked = false,
            });
        }

        return grants;
    }

    /// <summary>
    /// A profile authorizes nothing unless it names the database this process is wired to. Without a
    /// configured sandbox there is no target, so there is nothing a grant could authorize.
    /// </summary>
    public bool MatchesConfiguredSandbox(PlatformTargetProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return sandbox is not null &&
            string.Equals(profile.EndpointHost, sandbox.EndpointHost, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(profile.DatabaseName, sandbox.DatabaseName, StringComparison.Ordinal) &&
            string.Equals(profile.ExecutionIdentity, sandbox.ExecutionIdentity, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Owner identifiers are tenant-qualified; this is the one place that knows the shape.</summary>
public static class PlatformIdentity
{
    public static string WorkspaceOwner(WorkbenchActor actor, string projectId)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return $"{actor.OwnerId}/{projectId}";
    }

    public static bool TrySplitOwner(string? ownerId, out string tenantId, out string objectId)
    {
        tenantId = string.Empty;
        objectId = string.Empty;

        if (string.IsNullOrWhiteSpace(ownerId))
        {
            return false;
        }

        int separator = ownerId.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == ownerId.Length - 1)
        {
            return false;
        }

        tenantId = ownerId[..separator];
        objectId = ownerId[(separator + 1)..];
        return true;
    }
}

/// <summary>
/// The approval state machine and the project reads behind the authenticated APIs.
///
/// Nothing here trusts a caller beyond an identifier. Membership is looked up, the target profile is
/// looked up, the bindings come from the server's own view of the source and the sanitized plan, and
/// the decision path refuses the requester by identity rather than by asking them not to.
/// </summary>
public sealed class PlatformAccessService(
    IPlatformStateStore store,
    ISandboxTargetBinding? sandbox,
    Func<DateTimeOffset>? clock = null,
    ISandboxProjectBindingStore? sandboxProjects = null)
{
    /// <summary>Shortest and longest life an approval may be granted for.</summary>
    public static readonly TimeSpan MinimumLifetime = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(24);

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    private readonly ISandboxProjectBindingStore? _sandboxProjects =
        sandboxProjects ?? store as ISandboxProjectBindingStore;

    public IPlatformStateStore Store => store;

    public ISandboxTargetBinding? Sandbox => sandbox;

    public async Task<PlatformResult<PlatformProject>> CreateProjectAsync(
        WorkbenchActor actor,
        string? name,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (actor.TenantId.Length == 0 || actor.ObjectId.Length == 0)
        {
            return PlatformResult<PlatformProject>.Fail(401, "The caller has no tenant-scoped identity.");
        }

        string projectName = (name ?? string.Empty).Trim();
        if (projectName.Length is < 1 or > 120)
        {
            return PlatformResult<PlatformProject>.Fail(400, "A project name must contain between 1 and 120 characters.");
        }

        if (FleetGuardrails.ContainsPotentialSecret(projectName))
        {
            return PlatformResult<PlatformProject>.Fail(400, "That project name looked like credential material.");
        }

        PlatformOrganization organization = await store
            .EnsureOrganizationAsync(actor.TenantId, actor.TenantId, cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset now = _clock();
        PlatformProject project = new()
        {
            ProjectId = $"prj-{Guid.NewGuid():N}",
            OrganizationId = organization.OrganizationId,
            TenantId = actor.TenantId,
            Name = projectName,
            CreatedUtc = now,
        };

        // The founder is a member and an approver. Separation of duty is enforced by identity on each
        // decision, not by withholding the role, so a second member can approve the founder's request.
        PlatformMembership founder = new()
        {
            ProjectId = project.ProjectId,
            TenantId = actor.TenantId,
            ObjectId = actor.ObjectId,
            Roles = [WorkbenchRoles.MigrationOperator, WorkbenchRoles.SandboxApprover],
            CreatedUtc = now,
        };

        await store.CreateProjectAsync(project, founder, cancellationToken).ConfigureAwait(false);
        return PlatformResult<PlatformProject>.Ok(project);
    }

    public Task<IReadOnlyList<PlatformProject>> ProjectsAsync(WorkbenchActor actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return store.ProjectsForActorAsync(actor.TenantId, actor.ObjectId, cancellationToken);
    }

    /// <summary>
    /// Adds or updates a member of a project.
    ///
    /// Without this, a project founder could request an approval that nobody is able to decide, because
    /// separation of duty refuses the requester and there is no second member. Roles are drawn from a
    /// fixed list that does not include production approval: no API path in this build grants it.
    /// </summary>
    public async Task<PlatformResult<PlatformMembership>> AddMemberAsync(
        WorkbenchActor actor,
        string projectId,
        string? objectId,
        IReadOnlyList<string>? roles,
        CancellationToken cancellationToken)
    {
        PlatformResult<PlatformMembership> access =
            await RequireMembershipAsync(actor, projectId, WorkbenchRoles.SandboxApprover, cancellationToken).ConfigureAwait(false);

        if (!access.Succeeded)
        {
            return access;
        }

        string member = (objectId ?? string.Empty).Trim();
        if (member.Length is 0 or > 128 || member.Any(character => char.IsControl(character) || character == ':'))
        {
            return PlatformResult<PlatformMembership>.Fail(400, "A member is named by a directory object identifier.");
        }

        if (!string.Equals(
                actor.TenantId,
                WorkbenchAuthenticationOptions.DevelopmentTenantId,
                StringComparison.Ordinal))
        {
            if (!Guid.TryParse(member, out Guid directoryObjectId))
            {
                return PlatformResult<PlatformMembership>.Fail(400, "A deployed project member must be named by an Entra object ID.");
            }

            member = directoryObjectId.ToString("D");
        }

        string[] allowed = [WorkbenchRoles.MigrationOperator, WorkbenchRoles.SandboxApprover];
        string[] requested = [.. (roles ?? allowed)
            .Select(role => allowed.FirstOrDefault(known => string.Equals(known, role, StringComparison.OrdinalIgnoreCase)))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)];

        if (requested.Length == 0)
        {
            return PlatformResult<PlatformMembership>.Fail(
                400, $"A member holds {WorkbenchRoles.MigrationOperator}, {WorkbenchRoles.SandboxApprover}, or both.");
        }

        PlatformMembership? existing =
            await store.GetMembershipAsync(actor.TenantId, projectId, member, cancellationToken).ConfigureAwait(false);

        PlatformMembership? written = await store.UpsertMembershipAsync(
            new PlatformMembership
            {
                ProjectId = projectId,
                TenantId = actor.TenantId,
                ObjectId = member,
                Roles = requested,
                CreatedUtc = existing?.CreatedUtc ?? _clock(),
                RemovedUtc = null,
            },
            existing?.Version,
            cancellationToken).ConfigureAwait(false);

        return written is null
            ? PlatformResult<PlatformMembership>.Fail(409, "That membership changed since it was read.")
            : PlatformResult<PlatformMembership>.Ok(written);
    }

    /// <summary>
    /// Records the target this process is configured for as an immutable profile of the project.
    ///
    /// The caller chooses nothing, including the generated back-end stack: that comes from deployment
    /// configuration and is covered by the canonical hash, so changing it supersedes the profile and
    /// strands every approval issued against the old one. Without a configured sandbox there is no target
    /// identity to describe, and inventing one would produce a profile that authorizes a database that
    /// does not exist.
    /// </summary>
    public async Task<PlatformResult<PlatformTargetProfile>> EnsureConfiguredTargetProfileAsync(
        WorkbenchActor actor,
        string projectId,
        PlatformTargetProfileEnvironment environment,
        CancellationToken cancellationToken)
    {
        PlatformResult<PlatformMembership> access =
            await RequireMembershipAsync(actor, projectId, WorkbenchRoles.MigrationOperator, cancellationToken).ConfigureAwait(false);

        if (!access.Succeeded)
        {
            return PlatformResult<PlatformTargetProfile>.Fail(access.Status, access.Error);
        }

        if (sandbox is null)
        {
            return PlatformResult<PlatformTargetProfile>.Fail(
                409,
                "No sandbox database is configured on this server, so there is no target identity to record.");
        }

        if (!PlatformTargetProfileEnvironment.TryResolveBackEnd(environment.StackBackEnd, out BackEndStack backEnd))
        {
            return PlatformResult<PlatformTargetProfile>.Fail(
                409,
                $"This server is configured for a back-end stack it cannot generate, so there is no target " +
                $"identity to record. Set {PlatformTargetProfileEnvironment.BackEndVariable} to one of: " +
                $"{string.Join(", ", Enum.GetNames<BackEndStack>())}.");
        }

        PlatformTargetProfile candidate = new()
        {
            TargetProfileId = "sandbox",
            ProjectId = projectId,
            TenantId = actor.TenantId,
            Version = 1,
            AzureTenantId = environment.AzureTenantId,
            SubscriptionId = environment.SubscriptionId,
            ResourceGroup = environment.ResourceGroup,
            ResourceId = environment.ResourceId,
            Region = environment.Region,
            EndpointHost = sandbox.EndpointHost,
            DatabaseName = sandbox.DatabaseName,
            SchemaName = environment.SchemaName,
            ExecutionIdentity = sandbox.ExecutionIdentity,
            EnvironmentName = environment.EnvironmentName,
            StackDatabase = nameof(DatabaseTarget.PostgreSql),
            StackFrontEnd = nameof(FrontEndStack.React),
            StackBackEnd = backEnd.ToString(),
            CanonicalHash = string.Empty,
            CreatedUtc = _clock(),
        };

        candidate = candidate with { CanonicalHash = PlatformTargetProfiles.Hash(candidate) };

        if (!PlatformTargetProfiles.TryValidate(candidate, out PlatformTargetProfileRejection? rejection))
        {
            return PlatformResult<PlatformTargetProfile>.Fail(400, rejection.Reason);
        }

        for (int attempt = 0; attempt < 8; attempt++)
        {
            PlatformTargetProfile? existing = await store
                .GetTargetProfileAsync(actor.TenantId, projectId, candidate.TargetProfileId, version: null, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                // Same target, same profile. A different target is a new immutable version, never an edit.
                PlatformTargetProfile comparable = candidate with { Version = existing.Version };
                comparable = comparable with { CanonicalHash = PlatformTargetProfiles.Hash(comparable) };
                if (string.Equals(existing.CanonicalHash, comparable.CanonicalHash, StringComparison.Ordinal))
                {
                    return PlatformResult<PlatformTargetProfile>.Ok(existing);
                }

                candidate = candidate with { Version = existing.Version + 1 };
                candidate = candidate with { CanonicalHash = PlatformTargetProfiles.Hash(candidate) };
            }

            PlatformTargetProfile? stored = await store.CreateTargetProfileAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (stored is not null)
            {
                return PlatformResult<PlatformTargetProfile>.Ok(stored);
            }

            PlatformTargetProfile? concurrent = await store
                .GetTargetProfileAsync(
                    actor.TenantId, projectId, candidate.TargetProfileId, candidate.Version, cancellationToken)
                .ConfigureAwait(false);
            if (concurrent is not null &&
                string.Equals(concurrent.CanonicalHash, candidate.CanonicalHash, StringComparison.Ordinal))
            {
                return PlatformResult<PlatformTargetProfile>.Ok(concurrent);
            }
        }

        return PlatformResult<PlatformTargetProfile>.Fail(
            409, "The target profile changed repeatedly while a new immutable version was being recorded.");
    }

    public async Task<PlatformResult<SourceEnvironmentProfile>> EnsureSourceEnvironmentProfileAsync(
        WorkbenchActor actor,
        string projectId,
        SourceEnvironmentDeclaration declaration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        PlatformResult<PlatformMembership> access = await RequireMembershipAsync(
            actor, projectId, WorkbenchRoles.MigrationOperator, cancellationToken).ConfigureAwait(false);
        if (!access.Succeeded)
        {
            return PlatformResult<SourceEnvironmentProfile>.Fail(access.Status, access.Error);
        }

        for (int attempt = 0; attempt < 8; attempt++)
        {
            SourceEnvironmentProfile? existing = await store.GetSourceEnvironmentProfileAsync(
                actor.TenantId, projectId, declaration.SourceEnvironmentId, version: null, cancellationToken)
                .ConfigureAwait(false);
            int version = existing?.Version ?? 1;
            SourceEnvironmentProfile candidate;
            try
            {
                candidate = SourceEnvironmentProfiles.Create(
                    actor.TenantId, projectId, declaration.SourceEnvironmentId, version,
                    declaration.Name, declaration.Connector, declaration.ExpectedFormsVersion,
                    declaration.ExpectedDatabaseVersion, declaration.PathAlias, declaration.SchemaAllowlist,
                    declaration.SecretReferences, _clock());
            }
            catch (ArgumentException exception)
            {
                return PlatformResult<SourceEnvironmentProfile>.Fail(400, exception.Message);
            }

            if (existing is not null && string.Equals(existing.DeclarationHash, candidate.DeclarationHash, StringComparison.Ordinal))
            {
                return PlatformResult<SourceEnvironmentProfile>.Ok(existing);
            }
            if (existing is not null)
            {
                candidate = SourceEnvironmentProfiles.Create(
                    actor.TenantId, projectId, declaration.SourceEnvironmentId, existing.Version + 1,
                    declaration.Name, declaration.Connector, declaration.ExpectedFormsVersion,
                    declaration.ExpectedDatabaseVersion, declaration.PathAlias, declaration.SchemaAllowlist,
                    declaration.SecretReferences, _clock());
            }

            SourceEnvironmentProfile? stored = await store
                .CreateSourceEnvironmentProfileAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (stored is not null)
            {
                return PlatformResult<SourceEnvironmentProfile>.Ok(stored);
            }
        }

        return PlatformResult<SourceEnvironmentProfile>.Fail(
            409, "The source environment profile changed repeatedly while a new immutable version was being recorded.");
    }

    public async Task<PlatformResult<SourceEnvironmentProbeResult>> ProbeSourceEnvironmentAsync(
        WorkbenchActor actor,
        string projectId,
        string sourceEnvironmentId,
        ISourceEnvironmentProbe probe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        PlatformResult<PlatformMembership> access = await RequireMembershipAsync(
            actor, projectId, WorkbenchRoles.MigrationOperator, cancellationToken).ConfigureAwait(false);
        if (!access.Succeeded)
        {
            return PlatformResult<SourceEnvironmentProbeResult>.Fail(access.Status, access.Error);
        }
        for (int attempt = 0; attempt < 3; attempt++)
        {
            SourceEnvironmentProfile? current = await store.GetSourceEnvironmentProfileAsync(
                actor.TenantId, projectId, sourceEnvironmentId, version: null, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return PlatformResult<SourceEnvironmentProbeResult>.Fail(404, "The source environment profile was not found.");
            }

            SourceEnvironmentProbeResult result = await probe.ProbeAsync(current, cancellationToken).ConfigureAwait(false);
            SourceEnvironmentProfile observed;
            try
            {
                observed = SourceEnvironmentProfiles.RecordProbe(current, result, _clock());
            }
            catch (ArgumentException exception)
            {
                return PlatformResult<SourceEnvironmentProbeResult>.Fail(400, exception.Message);
            }
            if (current.Readiness == observed.Readiness &&
                string.Equals(current.ObservedFormsVersion, observed.ObservedFormsVersion, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(current.ObservedDatabaseVersion, observed.ObservedDatabaseVersion, StringComparison.OrdinalIgnoreCase) &&
                current.LastProbeCapabilities.SequenceEqual(observed.LastProbeCapabilities) &&
                current.LastBlockedPrerequisites.SequenceEqual(observed.LastBlockedPrerequisites) &&
                current.LastContradictions.SequenceEqual(observed.LastContradictions, StringComparer.Ordinal))
            {
                return PlatformResult<SourceEnvironmentProbeResult>.Ok(result);
            }
            SourceEnvironmentProfile? stored = await store.CreateSourceEnvironmentProfileAsync(observed, cancellationToken).ConfigureAwait(false);
            if (stored is not null)
            {
                return PlatformResult<SourceEnvironmentProbeResult>.Ok(result with
                {
                    ProfileVersion = stored.Version,
                    ProfileHash = stored.CanonicalHash,
                });
            }
        }
        return PlatformResult<SourceEnvironmentProbeResult>.Fail(
            409, "The source environment changed repeatedly while its probe result was being recorded.");
    }

    public async Task<PlatformResult<IReadOnlyList<PlatformApproval>>> ApprovalsAsync(
        WorkbenchActor actor,
        string projectId,
        CancellationToken cancellationToken)
    {
        PlatformResult<PlatformMembership> access =
            await RequireMembershipAsync(actor, projectId, null, cancellationToken).ConfigureAwait(false);

        if (!access.Succeeded)
        {
            return PlatformResult<IReadOnlyList<PlatformApproval>>.Fail(access.Status, access.Error);
        }

        return PlatformResult<IReadOnlyList<PlatformApproval>>.Ok(
            await store.ApprovalsForProjectAsync(actor.TenantId, projectId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<PlatformResult<PlatformApproval>> RequestAsync(
        WorkbenchActor actor,
        PlatformApprovalRequestInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Scope == WorkbenchMutationScope.ProductionWrite)
        {
            return PlatformResult<PlatformApproval>.Fail(
                409,
                "Production approval is not available in this increment. No path in this build issues a production grant.");
        }

        PlatformResult<PlatformMembership> access =
            await RequireMembershipAsync(actor, input.ProjectId, WorkbenchRoles.MigrationOperator, cancellationToken).ConfigureAwait(false);

        if (!access.Succeeded)
        {
            return PlatformResult<PlatformApproval>.Fail(access.Status, access.Error);
        }

        PlatformTargetProfile? profile = await store
            .GetTargetProfileAsync(actor.TenantId, input.ProjectId, input.TargetProfileId, version: null, cancellationToken)
            .ConfigureAwait(false);

        if (profile is null)
        {
            return PlatformResult<PlatformApproval>.Fail(404, "That project has no target profile with that identifier.");
        }

        // Same check the execute path runs, against the profile this approval is about to be bound to.
        // An approval for a stack the profile does not name must not exist at all, because the plan-input
        // hash would then happily authorize the run that asked for it.
        if (!WorkbenchExecution.TryMatchTargetProfile(
            input.RequestedTarget, WorkbenchTargetProfileStack.From(profile), out string incompatible))
        {
            return PlatformResult<PlatformApproval>.Fail(409, incompatible);
        }

        if (sandbox is null)
        {
            return PlatformResult<PlatformApproval>.Fail(
                409,
                "No sandbox database is configured on this server, so a sandbox write could not be authorized even if approved.");
        }

        if (!new PlatformAuthorizationStore(store, sandbox, () => _clock()).MatchesConfiguredSandbox(profile))
        {
            return PlatformResult<PlatformApproval>.Fail(
                409,
                "The stored target profile does not describe the database this server is configured to write to.");
        }

        if (input.SourceSnapshotHash.Length == 0 ||
            string.Equals(input.SourceSnapshotHash, WorkbenchTrustBoundary.NoSourceHash, StringComparison.Ordinal))
        {
            return PlatformResult<PlatformApproval>.Fail(
                400,
                "An approval binds to a source copy this server indexed. Acquire the source first.");
        }

        TimeSpan lifetime = input.Lifetime < MinimumLifetime ? MinimumLifetime
            : input.Lifetime > MaximumLifetime ? MaximumLifetime
            : input.Lifetime;

        string? notes = (input.Notes ?? string.Empty).Trim();
        if (notes.Length == 0)
        {
            notes = null;
        }
        else if (notes.Length > 2_000 || FleetGuardrails.ContainsPotentialSecret(notes))
        {
            return PlatformResult<PlatformApproval>.Fail(400, "That note was rejected before it was stored.");
        }

        if (input.Scope == WorkbenchMutationScope.SandboxDatabaseWrite)
        {
            if (_sandboxProjects is null)
            {
                return PlatformResult<PlatformApproval>.Fail(
                    409, "This deployment has no durable sandbox-project boundary, so it cannot authorize database writes.");
            }

            string boundProject = await _sandboxProjects
                .BindSandboxProjectAsync(actor.TenantId, input.ProjectId, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(boundProject, input.ProjectId, StringComparison.Ordinal))
            {
                return PlatformResult<PlatformApproval>.Fail(
                    409,
                    "This deployment has one shared sandbox database. A different project already owns its database-write boundary; use that project or deploy a separate isolated workbench.");
            }
        }

        DateTimeOffset now = _clock();
        PlatformApproval approval = new()
        {
            ApprovalId = $"apr-{Guid.NewGuid():N}",
            ProjectId = input.ProjectId,
            TenantId = actor.TenantId,
            RequestedByObjectId = actor.ObjectId,
            RequestedUtc = now,
            State = PlatformApprovalState.Requested,
            Scope = input.Scope,
            RequiredRole = WorkbenchRoles.MigrationOperator,
            EngagementId = input.EngagementId,
            SourceSnapshotHash = input.SourceSnapshotHash,
            PlanInputHash = input.PlanInputHash,
            TargetProfileId = profile.TargetProfileId,
            TargetProfileVersion = profile.Version,
            TargetProfileHash = profile.CanonicalHash,
            ExpiresUtc = now.Add(lifetime),
            RequestNotes = notes,
        };

        return PlatformResult<PlatformApproval>.Ok(
            await store.CreateApprovalAsync(approval, cancellationToken).ConfigureAwait(false));
    }

    public async Task<PlatformResult<PlatformApproval>> DecideAsync(
        WorkbenchActor actor,
        string approvalId,
        bool approve,
        int expectedVersion,
        string? notes,
        CancellationToken cancellationToken)
    {
        PlatformApproval? approval = await store.GetApprovalAsync(actor.TenantId, approvalId, cancellationToken).ConfigureAwait(false);
        if (approval is null)
        {
            return PlatformResult<PlatformApproval>.Fail(404, "No approval with that identifier exists for this tenant.");
        }

        PlatformResult<PlatformMembership> access =
            await RequireMembershipAsync(actor, approval.ProjectId, WorkbenchRoles.SandboxApprover, cancellationToken).ConfigureAwait(false);

        if (!access.Succeeded)
        {
            return PlatformResult<PlatformApproval>.Fail(access.Status, access.Error);
        }

        if (string.Equals(approval.RequestedByObjectId, actor.ObjectId, StringComparison.OrdinalIgnoreCase))
        {
            return PlatformResult<PlatformApproval>.Fail(
                403, "The requester cannot decide their own request. Another project approver must.");
        }

        if (approval.State != PlatformApprovalState.Requested)
        {
            return PlatformResult<PlatformApproval>.Fail(
                409, $"That request was already {approval.State.ToString().ToLowerInvariant()}.");
        }

        if (approval.ExpiresUtc <= _clock())
        {
            return PlatformResult<PlatformApproval>.Fail(409, "That request expired before it was decided.");
        }

        if (approve && approval.Scope == WorkbenchMutationScope.SandboxDatabaseWrite)
        {
            string? boundProject = _sandboxProjects is null
                ? null
                : await _sandboxProjects.GetSandboxProjectAsync(actor.TenantId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(boundProject, approval.ProjectId, StringComparison.Ordinal))
            {
                return PlatformResult<PlatformApproval>.Fail(
                    409, "This approval belongs to a project that does not own the shared sandbox database.");
            }
        }

        string? decisionNotes = Sanitize(notes);
        if (decisionNotes is { Length: > 2_000 })
        {
            return PlatformResult<PlatformApproval>.Fail(400, "That note was rejected before it was stored.");
        }

        PlatformApproval decided = approval with
        {
            State = approve ? PlatformApprovalState.Approved : PlatformApprovalState.Rejected,
            DecidedByObjectId = actor.ObjectId,
            DecidedUtc = _clock(),
            DecisionNotes = decisionNotes,
        };

        PlatformApproval? written = await store.UpdateApprovalAsync(decided, expectedVersion, cancellationToken).ConfigureAwait(false);
        return written is null
            ? PlatformResult<PlatformApproval>.Fail(409, "That approval changed since it was read. Re-read it and decide again.")
            : PlatformResult<PlatformApproval>.Ok(written);
    }

    public async Task<PlatformResult<PlatformApproval>> RevokeAsync(
        WorkbenchActor actor,
        string approvalId,
        int expectedVersion,
        string? notes,
        CancellationToken cancellationToken)
    {
        PlatformApproval? approval = await store.GetApprovalAsync(actor.TenantId, approvalId, cancellationToken).ConfigureAwait(false);
        if (approval is null)
        {
            return PlatformResult<PlatformApproval>.Fail(404, "No approval with that identifier exists for this tenant.");
        }

        PlatformResult<PlatformMembership> access =
            await RequireMembershipAsync(actor, approval.ProjectId, null, cancellationToken).ConfigureAwait(false);

        if (!access.Succeeded)
        {
            return PlatformResult<PlatformApproval>.Fail(access.Status, access.Error);
        }

        bool isRequester = string.Equals(approval.RequestedByObjectId, actor.ObjectId, StringComparison.OrdinalIgnoreCase);
        if (!isRequester && !access.Value!.HasRole(WorkbenchRoles.SandboxApprover))
        {
            return PlatformResult<PlatformApproval>.Fail(
                403, "Only the requester or a project approver can revoke this authorization.");
        }

        if (approval.State is PlatformApprovalState.Revoked or PlatformApprovalState.Rejected)
        {
            return PlatformResult<PlatformApproval>.Fail(
                409, $"That request is already {approval.State.ToString().ToLowerInvariant()}.");
        }

        PlatformApproval revoked = approval with
        {
            State = PlatformApprovalState.Revoked,
            RevokedByObjectId = actor.ObjectId,
            RevokedUtc = _clock(),
            RevocationNotes = Sanitize(notes),
        };

        PlatformApproval? written = await store.UpdateApprovalAsync(revoked, expectedVersion, cancellationToken).ConfigureAwait(false);
        return written is null
            ? PlatformResult<PlatformApproval>.Fail(409, "That approval changed since it was read. Re-read it and revoke again.")
            : PlatformResult<PlatformApproval>.Ok(written);
    }

    /// <summary>
    /// Membership is the gate for every project-scoped read and write. An object identifier without an
    /// active membership in this tenant's project reaches nothing, and the answer is the same 404 a
    /// non-existent project gets so project identifiers cannot be enumerated.
    /// </summary>
    public async Task<PlatformResult<PlatformMembership>> RequireMembershipAsync(
        WorkbenchActor actor,
        string projectId,
        string? requiredRole,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (actor.TenantId.Length == 0 || actor.ObjectId.Length == 0)
        {
            return PlatformResult<PlatformMembership>.Fail(401, "The caller has no tenant-scoped identity.");
        }

        if (string.IsNullOrWhiteSpace(projectId))
        {
            return PlatformResult<PlatformMembership>.Fail(400, "A project identifier is required.");
        }

        PlatformMembership? membership =
            await store.GetMembershipAsync(actor.TenantId, projectId, actor.ObjectId, cancellationToken).ConfigureAwait(false);

        if (membership is null || !membership.IsActive)
        {
            return PlatformResult<PlatformMembership>.Fail(404, "No such project is available to this caller.");
        }

        if (requiredRole is not null && !membership.HasRole(requiredRole))
        {
            return PlatformResult<PlatformMembership>.Fail(
                403, $"This action requires the {requiredRole} role in this project.");
        }

        return PlatformResult<PlatformMembership>.Ok(membership);
    }

    private static string? Sanitize(string? notes)
    {
        string value = (notes ?? string.Empty).Trim();
        return value.Length == 0 || FleetGuardrails.ContainsPotentialSecret(value) ? null : value;
    }
}

/// <summary>
/// The Azure coordinates the host was deployed with, used to complete a target profile.
///
/// These are deployment facts injected as environment values, not caller input. When a deployment does
/// not declare them the profile records the placeholder string it was actually given, so a reader can
/// see that the coordinate is undeclared rather than being shown an invented one.
/// </summary>
public sealed record PlatformTargetProfileEnvironment
{
    public const string Undeclared = "undeclared";

    /// <summary>The environment variable that names the generated back-end stack.</summary>
    public const string BackEndVariable = "TARGET_BACKEND_STACK";

    public required string AzureTenantId { get; init; }

    public required string SubscriptionId { get; init; }

    public required string ResourceGroup { get; init; }

    public required string ResourceId { get; init; }

    public required string Region { get; init; }

    public required string SchemaName { get; init; }

    public required string EnvironmentName { get; init; }

    /// <summary>
    /// Back-end stack this deployment is configured to generate, named by <see cref="BackEndStack"/>.
    ///
    /// It is a deployment fact for the same reason the endpoint is: the generated application shape is
    /// part of the canonical profile hash an approval is bound to, so letting a request pick it would let
    /// a caller redirect an approved run at a target identity nobody approved. The default keeps an
    /// unchanged deployment on the stack its existing approvals were issued against.
    /// </summary>
    public string StackBackEnd { get; init; } = nameof(BackEndStack.JavaSpringBoot);

    public static PlatformTargetProfileEnvironment Read(
        WorkbenchConfigurationLookup configuration,
        string environmentName,
        bool requireSandboxCoordinates = true)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string declaredBackEnd = Value(configuration, BackEndVariable, nameof(BackEndStack.JavaSpringBoot));
        if (!TryResolveBackEnd(declaredBackEnd, out BackEndStack _))
        {
            // Fail closed at startup rather than silently generating a stack nobody configured. The
            // rejected value is not echoed, because configuration is read from the same process
            // environment that carries credential material.
            throw new InvalidOperationException(
                $"{BackEndVariable} names a back-end stack this build cannot generate. Supported: " +
                $"{string.Join(", ", Enum.GetNames<BackEndStack>())}.");
        }

        PlatformTargetProfileEnvironment environment = new()
        {
            AzureTenantId = Value(configuration, "SANDBOX_AZURE_TENANT_ID"),
            SubscriptionId = Value(configuration, "SANDBOX_AZURE_SUBSCRIPTION_ID"),
            ResourceGroup = Value(configuration, "SANDBOX_AZURE_RESOURCE_GROUP"),
            ResourceId = Value(configuration, "SANDBOX_AZURE_RESOURCE_ID"),
            Region = Value(configuration, "SANDBOX_AZURE_REGION"),
            SchemaName = Value(configuration, "SANDBOX_PGSCHEMA", "public"),
            EnvironmentName = environmentName,
            StackBackEnd = declaredBackEnd,
        };

        if (requireSandboxCoordinates &&
            !string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
        {
            List<string> missing = [];
            if (environment.AzureTenantId == Undeclared) missing.Add("SANDBOX_AZURE_TENANT_ID");
            if (environment.SubscriptionId == Undeclared) missing.Add("SANDBOX_AZURE_SUBSCRIPTION_ID");
            if (environment.ResourceGroup == Undeclared) missing.Add("SANDBOX_AZURE_RESOURCE_GROUP");
            if (environment.ResourceId == Undeclared) missing.Add("SANDBOX_AZURE_RESOURCE_ID");
            if (environment.Region == Undeclared) missing.Add("SANDBOX_AZURE_REGION");

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"A deployed sandbox target requires complete Azure coordinates. Missing: {string.Join(", ", missing)}.");
            }
        }

        return environment;
    }

    /// <summary>
    /// Resolves a configured stack name to a stack this build can actually generate.
    ///
    /// Only the declared names are accepted. <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>
    /// also accepts the underlying number, which would let a configured "1" quietly select whichever
    /// stack happens to sit at that ordinal today rather than the one an operator meant to name.
    /// </summary>
    public static bool TryResolveBackEnd(string? declared, out BackEndStack stack)
    {
        string value = (declared ?? string.Empty).Trim();
        foreach (BackEndStack known in Enum.GetValues<BackEndStack>())
        {
            if (string.Equals(known.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                stack = known;
                return true;
            }
        }

        stack = default;
        return false;
    }

    private static string Value(WorkbenchConfigurationLookup configuration, string name, string fallback = Undeclared) =>
        configuration(name) is { Length: > 0 } value ? value.Trim() : fallback;
}
