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

    /// <summary>
    /// A prerequisite phase ran in this same run and failed, so this phase was not invoked. The outcome
    /// detail names the prerequisite; nothing was generated and nothing was attested.
    /// </summary>
    BlockedByDependency,
}

/// <summary>
/// One line of run progress.
///
/// <paramref name="Signal"/> carries the typed framing the browser renders. Adapter chatter leaves it
/// null and stays inspectable detail; the executor attaches a signal to the structural moments — the
/// authorization, each phase transition — because those are what a summary is entitled to describe.
/// </summary>
public sealed record ExecutionProgress(string Level, string Text, ProgressSignal? Signal = null);

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
    private const string RunPurpose =
        "Running the phases the planner authorized and writing their output into your session workspace.";

    private readonly WorkspaceWriter _workspace;
    private readonly Dictionary<MigrationPhase, IPhaseAdapter> _adapters = [];
    private readonly IPhaseMutationAuthorizer? _mutationAuthorizer;

    /// <summary>
    /// <paramref name="mutationAuthorizer"/> is consulted immediately before any phase that writes
    /// outside the session workspace. Omitting it keeps the planner's decision as the only gate, which
    /// is the contract in-process callers already have; a host that serves an untrusted caller supplies
    /// one, and supplies one that denies when it cannot establish authority.
    /// </summary>
    public MigrationExecutor(
        string workspaceRoot,
        IEnumerable<IPhaseAdapter>? adapters = null,
        IPhaseMutationAuthorizer? mutationAuthorizer = null)
    {
        _workspace = new WorkspaceWriter(workspaceRoot);
        _mutationAuthorizer = mutationAuthorizer;

        foreach (IPhaseAdapter adapter in adapters ?? DefaultAdapters())
        {
            _adapters[adapter.Phase] = adapter;
        }
    }

    public static IReadOnlyList<IPhaseAdapter> DefaultAdapters(
        IArtifactReviewer? reviewer = null,
        IDataMigrationGateway? dataGateway = null,
        Agents.CritiqueRepairOrchestrator? orchestrator = null,
        ProgramUnitRepairLoop? programUnitRepair = null,
        IApplicationBuildGateway? applicationBuild = null,
        IApplicationTestGateway? applicationTests = null,
        ITargetApplicationVerificationGateway? targetVerification = null) =>
    [
        new SourceAnalysisAdapter(),
        new SourceNormalizationAdapter(),
        new DatabaseConversionAdapter(reviewer, orchestrator),
        new ApplicationCodeConversionAdapter(reviewer),
        new BuildAndStaticValidationAdapter(applicationBuild),
        new GeneratedApplicationVerificationAdapter(applicationTests, targetVerification),
        new SandboxDataMigrationAdapter(dataGateway, programUnitRepair),
        new DataReconciliationAdapter(dataGateway),
    ];

    /// <summary>
    /// Phases an execution adapter is permitted to attest to. Documentation, conversion, and build phases
    /// have no matching <see cref="AttestationKind"/>, so they produce artifacts and no attestation rather
    /// than an attestation of the wrong kind. Human acceptance is deliberately absent: an adapter signing
    /// its own acceptance would defeat the production gate it exists to guard.
    /// </summary>
    private static AttestationKind? AttestationFor(MigrationPhase phase) => phase switch
    {
        MigrationPhase.GeneratedApplicationVerification => AttestationKind.GeneratedApplicationTestsPassed,
        MigrationPhase.DifferentialBehaviorTesting => AttestationKind.DifferentialBehaviorTestPassed,
        MigrationPhase.SandboxDataMigration => AttestationKind.SandboxMigrationCompleted,
        MigrationPhase.DataReconciliation => AttestationKind.DataReconciliationPassed,
        _ => null,
    };

    /// <summary>
    /// Phases whose output another phase reads, and the condition under which that prerequisite applies.
    ///
    /// The rule is deliberately narrow and stated once: when a dependency applies, the prerequisite has to
    /// have reached <see cref="PhaseExecutionState.Executed"/> in this run. Nothing else counts.
    /// <see cref="PhaseExecutionState.SkippedByPlanner"/> is tolerated only where the dependency does not
    /// apply at all — a database-only run has no Forms source for normalization to adjudicate — and
    /// <see cref="PhaseExecutionState.AdapterNotImplemented"/> is never success: a phase nobody ran refused
    /// nothing and confirmed nothing.
    ///
    /// The earlier version bound only on an explicit failure, which meant a normalization phase that was
    /// skipped or unimplemented left the application converter free to re-read the same tree and generate
    /// from it, which is the exact silent-success failure this fleet is built to prevent.
    /// </summary>
    private sealed record PhaseDependency(MigrationPhase Prerequisite, Func<DependencyScope, bool> Applies);

    /// <summary>Everything a dependency's applicability test may look at.</summary>
    private sealed record DependencyScope(
        MigrationRunRequest Request,
        WorkspaceWriter Workspace,
        MigrationRunPlan Plan,
        IReadOnlySet<MigrationPhase> Registered)
    {
        /// <summary>True when the run is set up to produce the prerequisite's output at all.</summary>
        public bool Produces(MigrationPhase phase) =>
            Registered.Contains(phase) && Plan.Phases.Any(candidate => candidate.Phase == phase);
    }

    private static readonly Dictionary<MigrationPhase, PhaseDependency[]> s_dependencies = new()
    {
        // Applies whenever the run has Forms source of any kind. Without it there is nothing to normalize
        // and a schema-only conversion is the honest outcome, so the phase may legitimately be skipped.
        [MigrationPhase.ApplicationCodeConversion] =
        [
            new(MigrationPhase.SourceNormalization, FormsSourceApplies),
        ],

        // Applies whenever this run is the one that produces the application tier. Building output the run
        // did not generate would validate whatever an earlier run happened to leave in the workspace.
        [MigrationPhase.BuildAndStaticValidation] =
        [
            new(MigrationPhase.ApplicationCodeConversion, scope => scope.Produces(MigrationPhase.ApplicationCodeConversion)),
        ],

        [MigrationPhase.DifferentialBehaviorTesting] =
        [
            new(MigrationPhase.GeneratedApplicationVerification, scope => scope.Produces(MigrationPhase.GeneratedApplicationVerification)),
        ],

        [MigrationPhase.GeneratedApplicationVerification] =
        [
            new(MigrationPhase.BuildAndStaticValidation, scope => scope.Produces(MigrationPhase.BuildAndStaticValidation)),
            new(MigrationPhase.DatabaseConversion, scope => scope.Produces(MigrationPhase.DatabaseConversion)),
        ],
    };

    /// <summary>
    /// Whether this run has Forms source for normalization to adjudicate.
    ///
    /// A workspace that exceeds an intake limit answers neither yes nor no, so it answers yes: the
    /// prerequisite applies, the conversion stays blocked, and the run cannot reach a generated
    /// application tier on the strength of a question nothing was able to settle.
    /// </summary>
    private static bool FormsSourceApplies(DependencyScope scope)
    {
        try
        {
            return FormsSourcePresence.Applies(scope.Request, scope.Workspace, WorkspacePath.Normalize(scope.Request.SourceRoot));
        }
        catch (WorkspaceLimitExceededException)
        {
            return true;
        }
    }

    /// <summary>The unmet prerequisite and why it is unmet, or nothing when every prerequisite is clear.</summary>
    private static (MigrationPhase Prerequisite, string Reason)? BlockingDependency(        MigrationPhase phase,
        IReadOnlyList<PhaseOutcome> completed,
        DependencyScope scope)
    {
        if (!s_dependencies.TryGetValue(phase, out PhaseDependency[]? required))
        {
            return null;
        }

        foreach (PhaseDependency dependency in required)
        {
            if (!dependency.Applies(scope))
            {
                continue;
            }

            PhaseOutcome? outcome = completed.FirstOrDefault(candidate => candidate.Phase == dependency.Prerequisite);

            if (outcome is { State: PhaseExecutionState.Executed })
            {
                continue;
            }

            return (dependency.Prerequisite, outcome?.State switch
            {
                PhaseExecutionState.Failed => "it failed in this run",
                PhaseExecutionState.BlockedByDependency => "it was itself blocked by an unmet prerequisite",
                PhaseExecutionState.SkippedByPlanner => "the planner did not authorize it, so nothing adjudicated the source it covers",
                PhaseExecutionState.AdapterNotImplemented => "no adapter is registered for it, so nothing adjudicated the source it covers",
                _ => "it did not run",
            });
        }

        return null;
    }

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

        void ReportPhase(string level, string text, string action, string observed, string nextAction, int? artifactCount = null)
        {
            ExecutionProgress entry = new(level, text, new ProgressSignal(
                ProgressOperations.MigrationRun,
                action,
                ProgressState.Running,
                RunPurpose,
                observed,
                nextAction,
                artifactCount is null ? null : "GeneratedFile",
                artifactCount));
            log.Add(entry);
            progress?.Invoke(entry);
        }

        bool canAttest = !string.IsNullOrWhiteSpace(operatorIdentity) && !FleetGuardrails.ContainsPotentialSecret(operatorIdentity);

        ReportPhase(
            "info",
            $"Planner authorized {plan.AuthorizedMode} of the requested {plan.RequestedMode} for {plan.EngagementId}/{plan.ApplicationName}.",
            ProgressActions.PlanAuthorized,
            $"The planner authorized {plan.AuthorizedMode} of the requested {plan.RequestedMode}.",
            "Running each authorized phase in lifecycle order.");

        if (plan.Phases.Count == 0)
        {
            Report("error", "The planner returned no phases. " + string.Join(" ", plan.Blockers));
            return new MigrationExecutionResult(plan, outcomes, artifacts, attestations, log);
        }

        if (!canAttest)
        {
            Report("warn", "No usable operator identity was supplied, so no attestation will be produced for any phase.");
        }

        // Declaration order of MigrationPhase is an identity scheme with frozen numbers, not a sequence.
        // Sorting by the enum would run source normalization after the conversion that depends on it.
        DependencyScope scope = new(request, _workspace, plan, _adapters.Keys.ToHashSet());

        foreach (PhasePlan phase in plan.Phases.OrderBy(phase => MigrationLifecycle.PositionOf(phase.Phase)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (phase.Status != PhaseStatus.Planned)
            {
                string detail = phase.Blockers.Count > 0 ? string.Join(" ", phase.Blockers) : "The planner did not authorize this phase.";
                ReportPhase(
                    "skip",
                    $"{phase.Phase}: {phase.Status}. {detail}",
                    ProgressActions.PhaseSkipped,
                    $"{phase.Phase} was not authorized, so no adapter was invoked.",
                    "Clear the blockers on this phase and generate the plan again.");
                outcomes.Add(new PhaseOutcome(phase.Phase, phase.Status, PhaseExecutionState.SkippedByPlanner, [], [], detail));
                continue;
            }

            if (!_adapters.TryGetValue(phase.Phase, out IPhaseAdapter? adapter))
            {
                const string Detail = "The phase is authorized but no execution adapter is registered for it. Nothing was generated and nothing was attested.";
                ReportPhase(
                    "warn",
                    $"{phase.Phase}: {Detail}",
                    ProgressActions.PhaseNoAdapter,
                    $"{phase.Phase} is authorized but has no adapter here, so nothing was generated.",
                    "This is a missing capability rather than a setup problem; the remaining phases continue.");
                outcomes.Add(new PhaseOutcome(phase.Phase, phase.Status, PhaseExecutionState.AdapterNotImplemented, [], [], Detail));
                continue;
            }

            if (BlockingDependency(phase.Phase, outcomes, scope) is { } blocking)
            {
                string detail =
                    $"{phase.Phase} was not run because its prerequisite {blocking.Prerequisite} did not complete successfully in this run: " +
                    $"{blocking.Reason}. Running it anyway would generate from source nothing in this run adjudicated, so nothing was " +
                    "generated and nothing was attested.";

                ReportPhase(
                    "error",
                    $"{phase.Phase}: {detail}",
                    ProgressActions.PhaseBlocked,
                    $"{phase.Phase} was not run because {blocking.Prerequisite} did not complete in this run.",
                    $"Resolve {blocking.Prerequisite} first; nothing was generated for this phase.");
                outcomes.Add(new PhaseOutcome(phase.Phase, phase.Status, PhaseExecutionState.BlockedByDependency, [], [detail], detail));
                continue;
            }

            // Checked here rather than at plan time: the planner answers what the run may do, and this
            // answers whether the side effect is still authorized at the moment it would happen.
            if (phase.Mutation is MutationClass.SandboxDatabaseWrite or MutationClass.ProductionWrite &&
                _mutationAuthorizer is not null)
            {
                MutationAuthorizationResult decision = await _mutationAuthorizer.AuthorizeAsync(
                    new MutationAuthorizationRequest(phase.Phase, phase.Mutation, request, operatorIdentity),
                    cancellationToken).ConfigureAwait(false);

                if (!decision.IsAuthorized)
                {
                    string detail =
                        $"{phase.Phase} writes outside this session workspace and was not authorized at the moment it " +
                        $"would have run: {decision.Reason} Nothing was written and nothing was attested.";

                    ReportPhase(
                        "error",
                        $"{phase.Phase}: {detail}",
                        ProgressActions.PhaseBlocked,
                        $"{phase.Phase} was refused by the server before any change was made.",
                        "Obtain a scoped authorization for this run before attempting this phase again.");
                    outcomes.Add(new PhaseOutcome(
                        phase.Phase, phase.Status, PhaseExecutionState.Failed, [], [detail], detail));
                    continue;
                }
            }

            ReportPhase(
                "info",
                $"{phase.Phase}: running {adapter.GetType().Name}.",
                ProgressActions.PhaseStarted,
                $"{phase.Phase} is running.",
                "Waiting for this phase to report what it wrote.");

            PhaseExecutionContext context = new(
                _workspace.Root,
                request.SourceRoot,
                request.OutputRoot,
                phase,
                request,
                Report)
            {
                CompletedPhases = [.. outcomes],
            };

            PhaseExecutionResult result;
            try
            {
                result = await adapter.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is WorkspaceLimitExceededException or WorkspacePathException
                or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                result = PhaseExecutionResult.Failure($"{adapter.GetType().Name} was stopped: {exception.Message}");
            }

            if (!result.Succeeded)
            {
                string detail = result.FailureReason ?? "The adapter reported failure without a reason.";
                ReportPhase(
                    "error",
                    $"{phase.Phase}: {detail}",
                    ProgressActions.PhaseFinished,
                    $"{phase.Phase} failed: {detail}",
                    "Read the phase report before continuing; nothing was attested for this phase.");
                artifacts.AddRange(result.Artifacts);
                outcomes.Add(new PhaseOutcome(
                    phase.Phase,
                    phase.Status,
                    PhaseExecutionState.Failed,
                    result.Artifacts,
                    result.Findings,
                    detail));
                continue;
            }

            artifacts.AddRange(result.Artifacts);
            outcomes.Add(new PhaseOutcome(phase.Phase, phase.Status, PhaseExecutionState.Executed, result.Artifacts, result.Findings, null));
            // No count rides on this frame: it would be this phase's total, and the consumer keeps
            // the latest count per artifact kind. The run's terminal frame carries the real total.
            ReportPhase(
                "done",
                $"{phase.Phase}: wrote {result.Artifacts.Count.ToString(CultureInfo.InvariantCulture)} artifacts.",
                ProgressActions.PhaseFinished,
                $"{phase.Phase} completed and wrote {result.Artifacts.Count.ToString(CultureInfo.InvariantCulture)} file(s) into the session workspace.",
                "Continuing with the next authorized phase.");

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
