// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using OracleFormsMigrationFleet.Fleet.Execution.Adapters;

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>What actually happened to a phase during a run.</summary>
public enum PhaseExecutionState
{
    /// <summary>The planner did not authorize the phase, so no adapter was invoked.</summary>
    SkippedByPlanner,

    /// <summary>The planner authorized the phase but no adapter is registered for it. Nothing was generated.</summary>
    AdapterNotImplemented,

    Executed,

    Failed,
}

public sealed record ExecutionProgress(string Level, string Text);

public sealed record PhaseOutcome(
    MigrationPhase Phase,
    PhaseStatus PlannedStatus,
    PhaseExecutionState State,
    IReadOnlyList<ArtifactReference> Artifacts,
    IReadOnlyList<string> Findings,
    string? Detail);

/// <summary>
/// Auditable result of one execution run. An artifact appears here only if it was written, and an
/// attestation appears here only if the phase backing it ran to completion and produced those artifacts.
/// </summary>
public sealed record MigrationExecutionResult(
    MigrationRunPlan Plan,
    IReadOnlyList<PhaseOutcome> Phases,
    IReadOnlyList<ArtifactReference> Artifacts,
    IReadOnlyList<MigrationAttestation> Attestations,
    IReadOnlyList<ExecutionProgress> Progress);

/// <summary>
/// Runs the phases <see cref="MigrationRunPlanner"/> authorized, and only those.
///
/// The executor calls the planner itself rather than accepting a plan from the caller, so a caller cannot
/// hand it a forged plan. Every phase whose resolved status is not <see cref="PhaseStatus.Planned"/> is
/// skipped without invoking an adapter. All adapter I/O is confined to the supplied workspace root.
/// </summary>
public sealed class MigrationExecutor
{
    private readonly WorkspaceWriter _workspace;
    private readonly Dictionary<MigrationPhase, IPhaseAdapter> _adapters = [];

    public MigrationExecutor(string workspaceRoot, IEnumerable<IPhaseAdapter>? adapters = null)
    {
        _workspace = new WorkspaceWriter(workspaceRoot);

        foreach (IPhaseAdapter adapter in adapters ?? DefaultAdapters())
        {
            _adapters[adapter.Phase] = adapter;
        }
    }

    public static IReadOnlyList<IPhaseAdapter> DefaultAdapters(
        IArtifactReviewer? reviewer = null,
        IDataMigrationGateway? dataGateway = null,
        Agents.CritiqueRepairOrchestrator? orchestrator = null) =>
    [
        new SourceAnalysisAdapter(),
        new DatabaseConversionAdapter(reviewer, orchestrator),
        new ApplicationCodeConversionAdapter(reviewer),
        new SandboxDataMigrationAdapter(dataGateway),
    ];

    /// <summary>
    /// Phases an execution adapter is permitted to attest to. Documentation, conversion, and build phases
    /// have no matching <see cref="AttestationKind"/>, so they produce artifacts and no attestation rather
    /// than an attestation of the wrong kind. Human acceptance is deliberately absent: an adapter signing
    /// its own acceptance would defeat the production gate it exists to guard.
    /// </summary>
    private static AttestationKind? AttestationFor(MigrationPhase phase) => phase switch
    {
        MigrationPhase.DifferentialBehaviorTesting => AttestationKind.DifferentialBehaviorTestPassed,
        MigrationPhase.SandboxDataMigration => AttestationKind.SandboxMigrationCompleted,
        MigrationPhase.DataReconciliation => AttestationKind.DataReconciliationPassed,
        _ => null,
    };

    public async Task<MigrationExecutionResult> ExecuteAsync(
        MigrationRunRequest request,
        string operatorIdentity,
        Action<ExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(request);

        List<ExecutionProgress> log = [];
        List<PhaseOutcome> outcomes = [];
        List<ArtifactReference> artifacts = [];
        List<MigrationAttestation> attestations = [];

        void Report(string level, string text)
        {
            ExecutionProgress entry = new(level, text);
            log.Add(entry);
            progress?.Invoke(entry);
        }

        bool canAttest = !string.IsNullOrWhiteSpace(operatorIdentity) && !FleetGuardrails.ContainsPotentialSecret(operatorIdentity);

        Report("info", $"Planner authorized {plan.AuthorizedMode} of the requested {plan.RequestedMode} for {plan.EngagementId}/{plan.ApplicationName}.");

        if (plan.Phases.Count == 0)
        {
            Report("error", "The planner returned no phases. " + string.Join(" ", plan.Blockers));
            return new MigrationExecutionResult(plan, outcomes, artifacts, attestations, log);
        }

        if (!canAttest)
        {
            Report("warn", "No usable operator identity was supplied, so no attestation will be produced for any phase.");
        }

        foreach (PhasePlan phase in plan.Phases.OrderBy(phase => phase.Phase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (phase.Status != PhaseStatus.Planned)
            {
                string detail = phase.Blockers.Count > 0 ? string.Join(" ", phase.Blockers) : "The planner did not authorize this phase.";
                Report("skip", $"{phase.Phase}: {phase.Status}. {detail}");
                outcomes.Add(new PhaseOutcome(phase.Phase, phase.Status, PhaseExecutionState.SkippedByPlanner, [], [], detail));
                continue;
            }

            if (!_adapters.TryGetValue(phase.Phase, out IPhaseAdapter? adapter))
            {
                const string Detail = "The phase is authorized but no execution adapter is registered for it. Nothing was generated and nothing was attested.";
                Report("warn", $"{phase.Phase}: {Detail}");
                outcomes.Add(new PhaseOutcome(phase.Phase, phase.Status, PhaseExecutionState.AdapterNotImplemented, [], [], Detail));
                continue;
            }

            Report("info", $"{phase.Phase}: running {adapter.GetType().Name}.");

            PhaseExecutionContext context = new(
                _workspace.Root,
                request.SourceRoot,
                request.OutputRoot,
                phase,
                request,
                Report);

            PhaseExecutionResult result;
            try
            {
                result = await adapter.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is WorkspacePathException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                result = PhaseExecutionResult.Failure($"{adapter.GetType().Name} was stopped: {exception.Message}");
            }

            if (!result.Succeeded)
            {
                string detail = result.FailureReason ?? "The adapter reported failure without a reason.";
                Report("error", $"{phase.Phase}: {detail}");
                outcomes.Add(new PhaseOutcome(phase.Phase, phase.Status, PhaseExecutionState.Failed, [], result.Findings, detail));
                continue;
            }

            artifacts.AddRange(result.Artifacts);
            outcomes.Add(new PhaseOutcome(phase.Phase, phase.Status, PhaseExecutionState.Executed, result.Artifacts, result.Findings, null));
            Report("done", $"{phase.Phase}: wrote {result.Artifacts.Count.ToString(CultureInfo.InvariantCulture)} artifacts.");

            if (AttestationFor(phase.Phase) is not AttestationKind kind)
            {
                Report("info", $"{phase.Phase}: no attestation kind describes this phase, so none was produced. The artifacts above are the only evidence.");
                continue;
            }

            if (!canAttest || result.Artifacts.Count == 0)
            {
                Report("warn", $"{phase.Phase}: completed without a signable attestation.");
                continue;
            }

            attestations.Add(new MigrationAttestation
            {
                Kind = kind,
                Succeeded = true,
                AttestedBy = operatorIdentity,
                Summary = $"{phase.Phase} completed by {adapter.GetType().Name} and wrote the cited artifacts.",
                Artifacts = result.Artifacts,
            });
        }

        return new MigrationExecutionResult(plan, outcomes, artifacts, attestations, log);
    }
}
