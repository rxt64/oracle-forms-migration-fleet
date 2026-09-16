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
