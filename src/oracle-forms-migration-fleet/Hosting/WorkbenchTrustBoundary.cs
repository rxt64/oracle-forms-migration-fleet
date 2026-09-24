// Copyright (c) Microsoft. All rights reserved.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>Roles this workbench recognises. A role is held or it is not; there is no implied hierarchy.</summary>
public static class WorkbenchRoles
{
    /// <summary>May request a run and may hold a grant for one.</summary>
    public const string MigrationOperator = "MigrationOperator";

    /// <summary>May approve someone else's sandbox request.</summary>
    public const string SandboxApprover = "SandboxApprover";

    /// <summary>May approve production writes. No path in this increment issues a production grant.</summary>
    public const string ProductionApprover = "ProductionApprover";
}

/// <summary>
/// The authenticated caller, as the host established it.
///
/// Every field comes from the host's authentication boundary and from nowhere else. There is no
/// constructor path that reads a request body, because a caller who can name their own roles has no
/// roles at all.
///
/// <see cref="OwnerId"/> is the workspace and grant key. Built through <see cref="ForTenant"/> it is
/// tenant-qualified, so the same object identifier presented under a different tenant is a different
/// owner and reaches none of the first one's workspaces, projects, or grants.
/// </summary>
public sealed record WorkbenchActor(string OwnerId, IReadOnlyList<string> Roles)
{
    /// <summary>Tenant the principal was issued by. Empty only for actors built before identity was tenant-scoped.</summary>
    public string TenantId { get; init; } = string.Empty;

    /// <summary>Directory object identifier of the principal.</summary>
    public string ObjectId { get; init; } = string.Empty;

    public static WorkbenchActor ForTenant(string tenantId, string objectId, IReadOnlyList<string> roles) =>
        new($"{tenantId}:{objectId}", roles) { TenantId = tenantId, ObjectId = objectId };

    public bool HasRole(string role) =>
        Roles.Any(held => string.Equals(held, role, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Side effects that reach outside the session workspace and therefore need their own grant.</summary>
public enum WorkbenchMutationScope
{
    SandboxDatabaseWrite,
    ProductionWrite,
    /// <summary>Persists and retrieves an approval lifecycle but can never authorize a side effect.</summary>
    ValidationOnly,
}

/// <summary>
/// One server-issued grant. Every field is a binding: the grant is valid for this actor, in this role,
/// on this engagement, against this exact source, this exact plan input, this exact target, for this one
/// class of side effect, until this instant. Nothing here can be supplied by a caller.
/// </summary>
public sealed record WorkbenchAuthorizationRecord
{
    public required string AuthorizationId { get; init; }

    public required string OwnerId { get; init; }

    /// <summary>Role the actor must hold at the moment the side effect is attempted.</summary>
    public required string RequiredRole { get; init; }

    public required string EngagementId { get; init; }

    public required string SourceSnapshotHash { get; init; }

    public required string PlanInputHash { get; init; }

    public required string TargetHash { get; init; }

    public required WorkbenchMutationScope Scope { get; init; }

    public required DateTimeOffset ExpiresUtc { get; init; }

    /// <summary>Tenant the grant was issued under. Empty means an untenanted legacy grant.</summary>
    public string TenantId { get; init; } = string.Empty;

    /// <summary>Project the grant belongs to. Empty means no project scoping was recorded.</summary>
    public string ProjectId { get; init; } = string.Empty;

    /// <summary>Identity and version of the server-owned target profile the grant names.</summary>
    public string TargetProfileId { get; init; } = string.Empty;

    public int TargetProfileVersion { get; init; }

    /// <summary>Identifier of the actor who approved it, carried so a materialized approval can cite them.</summary>
    public string ApprovedByObjectId { get; init; } = string.Empty;

    /// <summary>A revoked grant is kept for audit and never authorizes.</summary>
    public bool IsRevoked { get; init; }
}

/// <summary>The facts a decision is made against, assembled by the server from server-owned inputs.</summary>
public sealed record WorkbenchAuthorizationQuery(
    WorkbenchActor Actor,
    string EngagementId,
    string SourceSnapshotHash,
    string PlanInputHash,
    string TargetHash,
    WorkbenchMutationScope Scope,
    string TenantId = "",
    string ProjectId = "",
    string TargetProfileId = "",
    int TargetProfileVersion = 0);

/// <summary>A decision, and the reason a run report will carry. A denial is never silent.</summary>
public sealed record WorkbenchAuthorizationDecision(bool IsAuthorized, string Reason, string? AuthorizationId = null)
{
    /// <summary>Who approved the grant this decision matched, so a materialized approval can name them.</summary>
    public string ApprovedByObjectId { get; init; } = string.Empty;

    public static WorkbenchAuthorizationDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// Source of server-issued grants for one owner.
///
/// A store is read-only by design. Nothing in the request path may create a grant, so a caller cannot
/// turn an attempted side effect into permission for it. The lookup is asynchronous because the store
/// that actually holds grants is a database, and blocking a request thread on it would be the kind of
/// sync-over-async that stops being a style question under load.
/// </summary>
public interface IWorkbenchAuthorizationStore
{
    Task<IReadOnlyList<WorkbenchAuthorizationRecord>> ForOwnerAsync(string ownerId, CancellationToken cancellationToken);
}

/// <summary>
/// The store a deployment falls back to when no platform persistence is configured.
///
/// Returning nothing is the accurate answer when there is nowhere to hold a grant that survives a
/// replica restart, and it means the default API path authorizes no sandbox or production mutation.
/// Planning and artifact generation are unaffected: they write only inside the caller's own session
/// workspace.
/// </summary>
public sealed class NoWorkbenchAuthorizationStore : IWorkbenchAuthorizationStore
{
    public static NoWorkbenchAuthorizationStore Instance { get; } = new();

    public Task<IReadOnlyList<WorkbenchAuthorizationRecord>> ForOwnerAsync(string ownerId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkbenchAuthorizationRecord>>([]);
}

/// <summary>
/// Decides whether a server-issued grant covers the side effect about to happen.
///
/// Every binding is checked, and any mismatch denies. A grant is not a capability the caller holds; it
/// is a statement the server made about one specific run, and a run that has drifted from it in any
/// respect — different owner, engagement, source, plan input, target, scope, or role, or simply later
/// than the expiry — is a different run.
/// </summary>
public sealed class WorkbenchAuthorizationService(IWorkbenchAuthorizationStore? store = null)
{
    private readonly IWorkbenchAuthorizationStore _store = store ?? NoWorkbenchAuthorizationStore.Instance;

    public async Task<WorkbenchAuthorizationDecision> AuthorizeAsync(
        WorkbenchAuthorizationQuery query,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(query.Actor.OwnerId))
        {
            return WorkbenchAuthorizationDecision.Deny("The caller was not authenticated.");
        }

        IReadOnlyList<WorkbenchAuthorizationRecord> candidates =
            await _store.ForOwnerAsync(query.Actor.OwnerId, cancellationToken).ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return WorkbenchAuthorizationDecision.Deny(
                "No server-issued authorization exists for this run.");
        }

        string? matchingDenial = null;
        foreach (WorkbenchAuthorizationRecord record in candidates)
        {
            if (!string.Equals(record.OwnerId, query.Actor.OwnerId, StringComparison.Ordinal) ||
                record.Scope != query.Scope ||
                !string.Equals(record.EngagementId, query.EngagementId, StringComparison.Ordinal))
            {
                continue;
            }

            if (record.IsRevoked)
            {
                matchingDenial ??= "The authorization for this run was revoked.";
                continue;
            }

            if (record.ExpiresUtc <= nowUtc)
            {
                matchingDenial ??= "The authorization for this run has expired.";
                continue;
            }

            if (!string.Equals(record.TenantId, query.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                matchingDenial ??= "The authorization was issued under a different tenant.";
                continue;
            }

            if (!string.Equals(record.ProjectId, query.ProjectId, StringComparison.Ordinal))
            {
                matchingDenial ??= "The authorization was issued for a different project.";
                continue;
            }

            if (!string.Equals(record.TargetProfileId, query.TargetProfileId, StringComparison.Ordinal) ||
                record.TargetProfileVersion != query.TargetProfileVersion)
            {
                matchingDenial ??= "The target profile changed after this authorization was issued.";
                continue;
            }

            if (!FixedTimeEquals(record.SourceSnapshotHash, query.SourceSnapshotHash))
            {
                matchingDenial ??= "The source copy is not the one this authorization was issued against.";
                continue;
            }

            if (!FixedTimeEquals(record.PlanInputHash, query.PlanInputHash))
            {
                matchingDenial ??= "The run inputs changed after this authorization was issued.";
                continue;
            }

            if (!FixedTimeEquals(record.TargetHash, query.TargetHash))
            {
                matchingDenial ??= "The migration target is not the one this authorization was issued against.";
                continue;
            }

            return new WorkbenchAuthorizationDecision(true, "A server-issued authorization covers this run.", record.AuthorizationId)
            {
                ApprovedByObjectId = record.ApprovedByObjectId,
            };
        }

        return WorkbenchAuthorizationDecision.Deny(
            matchingDenial ?? "No server-issued authorization covers this owner, engagement, and scope.");
    }

    private static bool FixedTimeEquals(string left, string right) =>
        left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
}

/// <summary>What the server decided about one inbound request, after discarding everything it cannot verify.</summary>
public sealed record WorkbenchRequestPreparation(
    MigrationRunRequest Request,
    string SourceSnapshotHash,
    string PlanInputHash,
    string TargetHash,
    bool SourceResolved);

/// <summary>
/// The boundary between what a caller said and what the server is willing to believe.
///
/// Three claims in <see cref="MigrationRunRequest"/> are authority, not data: whether a piece of evidence
/// is verified, whether a human approved a gate, and whether a phase attested to its own completion. A
/// browser can put any of them in a JSON body, so none of them is accepted here. Verified evidence is
/// re-derived from the owner-resolved source copy the server itself indexed, approvals are cleared unless
/// a server-issued grant is materialized, and attestations are dropped outright.
///
/// The caller's own declarations survive as declarations. They stay in the request, marked unverified and
/// stripped of signals, because an operator saying "we have a test baseline" is useful context and is not
/// the same statement as the server having seen one.
/// </summary>
public static class WorkbenchTrustBoundary
{
    /// <summary>Role a grant must name before this workbench will write to a sandbox or to production.</summary>
    public const string MigrationOperatorRole = "MigrationOperator";

    private const string ServerEvidenceSource = "server:source-workspace-index";
    private const string DeclaredEvidenceSource = "operator-declared:unverified";

    /// <summary>
    /// Rewrites an inbound request into the one the server is prepared to act on, and computes the
    /// bindings an authorization would have to match.
    ///
    /// <paramref name="facts"/> is the owner-resolved source copy, or null when the caller is working
    /// from a manually described estate. With no copy there is nothing the server has read, so every
    /// piece of evidence stays unverified and the source hash matches no grant.
    /// </summary>
    public static WorkbenchRequestPreparation Prepare(MigrationRunRequest request, SourceWorkspaceFacts? facts)
    {
        ArgumentNullException.ThrowIfNull(request);

        MigrationRunRequest sanitized = request with
        {
            Evidence = Evidence(request.Evidence, facts?.Summary),

            // Cleared, not honoured. A grant materializes an approval; a JSON property never does.
            PlanApproval = HumanApproval.Pending,
            ExecutionApproval = HumanApproval.Pending,
            ProductionApproval = HumanApproval.Pending,

            // An attestation is a statement by the adapter that ran a phase. A caller has run nothing.
            Attestations = [],
        };

        return new WorkbenchRequestPreparation(
            sanitized,
            facts?.SnapshotHash ?? NoSourceHash,
            PlanInputHash(sanitized),
            TargetHash(sanitized.Target),
            facts is not null);
    }

    /// <summary>Sanitizes a read-only planning request without computing or carrying a source digest.</summary>
    public static MigrationRunRequest PreparePlan(MigrationRunRequest request, SourceWorkspaceSummary? summary)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request with
        {
            Evidence = Evidence(request.Evidence, summary),
            PlanApproval = HumanApproval.Pending,
            ExecutionApproval = HumanApproval.Pending,
            ProductionApproval = HumanApproval.Pending,
            Attestations = [],
        };
    }

    /// <summary>
    /// Stands in for the source hash when no owned copy backs the run. It is a label rather than a
    /// digest, so it can never equal a hash an authorization was issued against.
    /// </summary>
    public const string NoSourceHash = "no-source-workspace";

    /// <summary>
    /// Verified evidence, derived only from what the server indexed itself, plus the caller's own
    /// declarations kept as unverified.
    ///
    /// Identifiers and sources on the verified items are server-owned so a report cannot be made to cite
    /// an operator's string as if the index had produced it. Declared items never carry signals: signals
    /// are machine-readable facts extracted from an artifact, and no artifact was read.
    /// </summary>
    private static IReadOnlyList<EvidenceItem> Evidence(
        IReadOnlyList<EvidenceItem>? declared,
        SourceWorkspaceSummary? summary)
    {
        List<EvidenceItem> evidence = [];
        HashSet<EvidenceKind> derived = [];

        foreach (SourceArtifact artifact in summary?.Artifacts ?? [])
        {
            if (!Enum.TryParse(artifact.Kind, ignoreCase: false, out EvidenceKind kind) || !derived.Add(kind))
            {
                continue;
            }

            evidence.Add(new EvidenceItem
            {
                Id = $"SRC-{kind}",
                Kind = kind,
                Source = ServerEvidenceSource,
                Summary = $"{artifact.Count} file(s) recognised as {kind} by name in the source copy this server indexed.",
                IsVerified = true,
                Signals = [],
            });
        }

        int index = 0;
        foreach (EvidenceItem item in declared ?? [])
        {
            if (!Enum.IsDefined(item.Kind) || derived.Contains(item.Kind))
            {
                continue;
            }

            index++;
            evidence.Add(new EvidenceItem
            {
                Id = $"DECL-{index}",
                Kind = item.Kind,
                Source = DeclaredEvidenceSource,
                Summary = $"{item.Kind} declared by the operator. Nothing on this server has read it.",
                IsVerified = false,
                Signals = [],
            });
        }

        return evidence;
    }

    /// <summary>
    /// Canonical identity of the run inputs an authorization is issued against.
    ///
    /// Structured serialization of a purpose-built shape, not concatenated text: a field cannot be made
    /// to look like the next one by embedding a separator, and evidence is ordered so that two requests
    /// differing only in list order hash the same.
    /// </summary>
    public static string PlanInputHash(MigrationRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        CanonicalPlanInput canonical = new(
            request.EngagementId,
            request.ApplicationName,
            request.RequestedMode.ToString(),
            request.Target?.Database.ToString() ?? "none",
            request.Target?.FrontEnd.ToString() ?? "none",
            request.Target?.BackEnd.ToString() ?? "none",
            request.OracleFormsVersion,
            request.OracleDatabaseVersion,
            WorkspacePath.Normalize(request.SourceRoot),
            WorkspacePath.Normalize(request.OutputRoot),

            // A run that generates under recorded decisions is not the run an approval over no ledger was
            // issued for, and moving to a different ledger is a different run again. Both differences have
            // to invalidate the grant, so the locator is part of the identity it was issued against.
            request.DispositionLedgerId ?? string.Empty,
            [.. (request.Evidence ?? [])
                .Select(item => new CanonicalEvidence(item.Kind.ToString(), item.Source, item.IsVerified))
                .OrderBy(item => item.Kind, StringComparer.Ordinal)
                .ThenBy(item => item.Source, StringComparer.Ordinal)
                .ThenBy(item => item.IsVerified)]);

        return Digest(canonical);
    }

    public static string TargetHash(TargetStack? target) =>
        target is null
            ? Digest(new CanonicalTarget("none", "none", "none"))
            : Digest(new CanonicalTarget(
                target.Database.ToString(),
                target.FrontEnd.ToString(),
                target.BackEnd.ToString()));

    private static string Digest<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, CanonicalOptions))).ToLowerInvariant();

    private sealed record CanonicalEvidence(string Kind, string Source, bool IsVerified);

    private sealed record CanonicalPlanInput(
        string EngagementId,
        string ApplicationName,
        string RequestedMode,
        string TargetDatabase,
        string TargetFrontEnd,
        string TargetBackEnd,
        string OracleFormsVersion,
        string OracleDatabaseVersion,
        string SourceRoot,
        string OutputRoot,
        string DispositionLedgerId,
        IReadOnlyList<CanonicalEvidence> Evidence);

    private sealed record CanonicalTarget(string Database, string FrontEnd, string BackEnd);

    /// <summary>Fixed settings so a digest depends on the values and not on a host's serializer defaults.</summary>
    private static readonly JsonSerializerOptions CanonicalOptions = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };
}

/// <summary>
/// The authorizer the HTTP path hands to <see cref="MigrationExecutor"/>.
///
/// It re-asks the question immediately before each sandbox or production phase rather than answering it
/// once at the start of the run, so a grant that expires or is revoked part way through stops the next
/// side effect rather than the next run. It fails closed: any answer other than an explicit match denies.
/// </summary>
public sealed class WorkbenchMutationAuthorizer(
    WorkbenchAuthorizationService service,
    WorkbenchActor actor,
    string sourceSnapshotHash,
    string planInputHash,
    string targetHash,
    Func<DateTimeOffset>? clock = null,
    string projectId = "",
    string targetProfileId = "",
    int targetProfileVersion = 0) : IPhaseMutationAuthorizer
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public Task<MutationAuthorizationResult> AuthorizeAsync(
        MutationAuthorizationRequest request,
        CancellationToken cancellationToken) =>
        AuthorizeCoreAsync(request, cancellationToken);

    /// <summary>
    /// Re-asks the same question the phase gate asked, for use at a gateway call boundary where a
    /// single phase may make several external calls over a long period.
    /// </summary>
    public Task<MutationAuthorizationResult> RecheckAsync(
        MigrationRunRequest request,
        WorkbenchMutationScope scope,
        CancellationToken cancellationToken) =>
        DecideAsync(request, scope, cancellationToken);

    private async Task<MutationAuthorizationResult> AuthorizeCoreAsync(
        MutationAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        WorkbenchMutationScope? scope = request.Mutation switch
        {
            MutationClass.SandboxDatabaseWrite => WorkbenchMutationScope.SandboxDatabaseWrite,
            MutationClass.ProductionWrite => WorkbenchMutationScope.ProductionWrite,

            // Reading the approved sandbox target is covered by the sandbox grant the operator already
            // issued for that exact target profile: the grant names the destination this read opens, and
            // it is the narrowest scope that does. It is mapped explicitly here rather than by declaring
            // the phase a write, so the phase says what it does and the grant it is held to is stated.
            MutationClass.ExternalTargetRead => WorkbenchMutationScope.SandboxDatabaseWrite,
            _ => null,
        };

        if (scope is null)
        {
            return MutationAuthorizationResult.Deny(
                "This phase was offered for authorization without a side effect that needs one.");
        }

        return await DecideAsync(request.Request, scope.Value, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MutationAuthorizationResult> DecideAsync(
        MigrationRunRequest request,
        WorkbenchMutationScope scope,
        CancellationToken cancellationToken)
    {
        WorkbenchAuthorizationDecision decision = await service.AuthorizeAsync(
            new WorkbenchAuthorizationQuery(
                actor,
                request.EngagementId,
                sourceSnapshotHash,
                planInputHash,
                targetHash,
                scope,
                actor.TenantId,
                projectId,
                targetProfileId,
                targetProfileVersion),
            _clock(),
            cancellationToken).ConfigureAwait(false);

        return decision.IsAuthorized
            ? MutationAuthorizationResult.Allow(decision.Reason)
            : MutationAuthorizationResult.Deny(decision.Reason);
    }
}

/// <summary>
/// Re-checks the grant at the gateway call boundary.
///
/// The phase gate answers once per phase. A data migration phase can spend minutes inside one adapter
/// making many external calls, so the grant is re-asked before each one. A revocation part way through
/// therefore stops the next statement rather than the next run. Nothing here claims to undo a write that
/// already committed: it stops at the next safe point and says so.
/// </summary>
public sealed class AuthorizingDataMigrationGateway(
    IDataMigrationGateway inner,
    WorkbenchMutationAuthorizer authorizer,
    MigrationRunRequest request) : IDataMigrationGateway
{
    public async Task<SchemaDeploymentOutcome> PrepareAsync(
        IReadOnlyList<string> statements,
        CancellationToken cancellationToken)
    {
        await GuardAsync(cancellationToken).ConfigureAwait(false);
        return await inner.PrepareAsync(statements, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TableRowCount>> CountAsync(
        IReadOnlyList<string> tables,
        CancellationToken cancellationToken)
    {
        await GuardAsync(cancellationToken).ConfigureAwait(false);
        return await inner.CountAsync(tables, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IReadOnlyList<string?>>> FetchAsync(
        string table,
        IReadOnlyList<string> columns,
        int maxRows,
        CancellationToken cancellationToken)
    {
        await GuardAsync(cancellationToken).ConfigureAwait(false);
        return await inner.FetchAsync(table, columns, maxRows, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataMigrationOutcome> ApplyAsync(
        IReadOnlyList<DataMigrationStatement> statements,
        IReadOnlyList<string> tables,
        CancellationToken cancellationToken)
    {
        await GuardAsync(cancellationToken).ConfigureAwait(false);
        return await inner.ApplyAsync(statements, tables, cancellationToken).ConfigureAwait(false);
    }

    private async Task GuardAsync(CancellationToken cancellationToken)
    {
        MutationAuthorizationResult decision = await authorizer
            .RecheckAsync(request, WorkbenchMutationScope.SandboxDatabaseWrite, cancellationToken)
            .ConfigureAwait(false);

        if (!decision.IsAuthorized)
        {
            throw new UnauthorizedAccessException(decision.Reason);
        }
    }
}

/// <summary>
/// Re-checks the grant immediately before the approved target is read.
///
/// The phase gate answers once, when the verification phase starts. This answers again at the only point
/// that matters — the instant before a connection to a customer database is opened — so a grant revoked
/// while the run sat in the queue, or between the migration phase and this one, stops the read rather
/// than being discovered afterwards. It fails closed: a denial throws, the adapter records the phase as
/// failed, and nothing is offered to the ledger.
/// </summary>
public sealed class AuthorizingEntryRuntimeVerificationGateway(
    IEntryRuntimeVerificationGateway inner,
    WorkbenchMutationAuthorizer authorizer,
    MigrationRunRequest request) : IEntryRuntimeVerificationGateway
{
    public EntryVerificationTargetBinding Target => inner.Target;

    public async Task<EntryRuntimeVerificationRun> InspectAsync(
        EntryVerificationTargetBinding approved,
        IReadOnlyList<EntryVerificationExpectation> expectations,
        CancellationToken cancellationToken)
    {
        MutationAuthorizationResult decision = await authorizer
            .RecheckAsync(request, WorkbenchMutationScope.SandboxDatabaseWrite, cancellationToken)
            .ConfigureAwait(false);

        if (!decision.IsAuthorized)
        {
            throw new UnauthorizedAccessException(decision.Reason);
        }

        return await inner.InspectAsync(approved, expectations, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Re-checks the grant immediately before the bundle is handed to the trusted builder.
///
/// The deployment phase establishes its bindings, resolves a destination, and re-reads the server's
/// authorization over the run's source before it dispatches. None of that asks whether the operator's
/// grant for this run still holds at the instant the builder is reached, and a build that starts is a
/// build that produces an image and a revision this process cannot withdraw. It fails closed: a denial
/// throws, the phase is recorded failed, and nothing is dispatched.
/// </summary>
public sealed class AuthorizingTargetApplicationDeploymentGateway(
    ITargetApplicationDeploymentGateway inner,
    WorkbenchMutationAuthorizer authorizer,
    MigrationRunRequest request) : ITargetApplicationDeploymentGateway
{
    public string Description => inner.Description;

    public async Task<TargetDeploymentResult> PublishAsync(
        TargetDeploymentRequest deployment,
        CancellationToken cancellationToken)
    {
        MutationAuthorizationResult decision = await authorizer
            .RecheckAsync(request, WorkbenchMutationScope.SandboxDatabaseWrite, cancellationToken)
            .ConfigureAwait(false);

        if (!decision.IsAuthorized)
        {
            throw new UnauthorizedAccessException(decision.Reason);
        }

        return await inner.PublishAsync(deployment, cancellationToken).ConfigureAwait(false);
    }
}
