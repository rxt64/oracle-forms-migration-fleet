// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Fleet.Execution.Adapters;

/// <summary>
/// Everything an adapter is allowed to know about the run. There is no connection string, credential, or
/// endpoint here by design: adapters in this layer only read and write inside <see cref="WorkspaceRoot"/>.
/// </summary>
public sealed record PhaseExecutionContext(
    string WorkspaceRoot,
    string SourceRoot,
    string OutputRoot,
    PhasePlan Plan,
    MigrationRunRequest Request,
    Action<string, string> Report)
{
    private WorkspaceWriter? _workspace;

    public WorkspaceWriter Workspace => _workspace ??= new WorkspaceWriter(WorkspaceRoot);

    /// <summary>
    /// Outcomes of the phases the executor already ran in this run, in order. An adapter reads this to
    /// find out whether an upstream phase produced the model it should consume; it is never a gate the
    /// adapter can open for itself, because the executor refuses a dependent phase before calling it.
    /// </summary>
    public IReadOnlyList<PhaseOutcome> CompletedPhases { get; init; } = [];

    /// <summary>
    /// The server's authority over generating from this run's source, or null when the host established
    /// none.
    ///
    /// Only the host sets this, and it sets a provider rather than a decision: a generation phase asks at
    /// the moment it is about to emit, so a disposition changed between the operator's review and the
    /// phase is a disposition this run does not generate under. It is never read out of the workspace, the
    /// request, or a mapping manifest, because an authorization an untrusted document can supply
    /// authorizes nothing.
    /// </summary>
    public IGenerationAuthorizationProvider? AuthorizationProvider { get; init; }

    /// <summary>
    /// The server's authority over verifying what this run generated, or null when the host established
    /// none.
    ///
    /// Like <see cref="AuthorizationProvider"/> it is a provider rather than a decision, and for the same
    /// reason: the verification phase asks at the moment it is about to read the approved target, so a
    /// generation the server has not durably recorded, or a decision that moved since it was generated,
    /// is answered as it stands then. A phase with no provider reads no database.
    /// </summary>
    public IEntryVerificationGenerationProvider? VerificationProvider { get; init; }

    /// <summary>
    /// The server's authority over where this run may publish a generated application tier, or null when
    /// the host resolved none.
    ///
    /// A provider again, and for the sharpest version of the same reason: the destination is read from the
    /// project's immutable target profile and its effective approval at the moment the deployment phase is
    /// about to hand bytes to a builder, so an approval revoked or expired while the run was working is an
    /// approval this run does not publish under. It is never read out of the workspace or a request body,
    /// because a destination an untrusted document can name is a destination a caller can redirect.
    /// </summary>
    public ITargetDeploymentAuthorityProvider? DeploymentAuthorityProvider { get; init; }

    public void Info(string text) => Report?.Invoke("info", text);

    public void Warn(string text) => Report?.Invoke("warn", text);
}

public sealed record PhaseExecutionResult(
    bool Succeeded,
    IReadOnlyList<ArtifactReference> Artifacts,
    IReadOnlyList<string> Findings,
    string? FailureReason)
{
    public static PhaseExecutionResult Success(
        IReadOnlyList<ArtifactReference> artifacts,
        IReadOnlyList<string>? findings = null) => new(true, artifacts, findings ?? [], null);

    public static PhaseExecutionResult Failure(
        string reason,
        IReadOnlyList<string>? findings = null) => new(false, [], findings ?? [], reason);
}

/// <summary>
/// Performs the work of one lifecycle phase. The executor only ever calls an adapter for a phase the
/// planner resolved to <see cref="PhaseStatus.Planned"/>; an adapter never re-opens a gate for itself.
/// </summary>
public interface IPhaseAdapter
{
    MigrationPhase Phase { get; }

    Task<PhaseExecutionResult> ExecuteAsync(PhaseExecutionContext context, CancellationToken cancellationToken);
}
