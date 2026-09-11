// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Fleet;

/// <summary>The fixed stage order the orchestrator walks.</summary>
public static class MigrationStageSequence
{
    private static readonly MigrationStage[] s_ordered =
    [
        MigrationStage.Intake,
        MigrationStage.InventoryAnalysis,
        MigrationStage.DependencyMapping,
        MigrationStage.TargetPlatformAdvisory,
        MigrationStage.ConversionPlanning,
        MigrationStage.ValidationReview,
        MigrationStage.HumanApproval,
        MigrationStage.Completed,
    ];

    public static IReadOnlyList<MigrationStage> Ordered => s_ordered;

    public static MigrationStage? Next(MigrationStage stage)
    {
        int index = Array.IndexOf(s_ordered, stage);
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown stage.");
        }

        return index + 1 < s_ordered.Length ? s_ordered[index + 1] : null;
    }

    /// <summary>True when a stage status halts the pipeline.</summary>
    public static bool IsHalting(StageStatus status) =>
        status is StageStatus.BlockedOnEvidence or StageStatus.BlockedOnApproval or StageStatus.Rejected;
}

/// <summary>
/// Deterministic coordinator for the specialist roles. Contains no model, network, or Azure
/// dependency so that stage progression is fully unit-testable.
/// </summary>
public static class MigrationFleetOrchestrator
{
    public static MigrationPlan Run(MigrationAssessmentRequest? request)
    {
        request ??= new MigrationAssessmentRequest
        {
            EngagementId = string.Empty,
            ApplicationName = string.Empty,
        };

        List<StageResult> stages = [];
        List<string> assumptions = [];
        List<string> blockers = [];

        StageResult intake = RunIntake(request);
        stages.Add(intake);
        if (MigrationStageSequence.IsHalting(intake.Status))
        {
            return Build(request, stages, EmptyRecommendation(intake), [], assumptions, blockers);
        }

        StageResult inventory = RunInventory(request);
        stages.Add(inventory);
        if (MigrationStageSequence.IsHalting(inventory.Status))
        {
            return Build(request, stages, EmptyRecommendation(inventory), [], assumptions, blockers);
        }

        StageResult dependencies = RunDependencyMapping(request);
        stages.Add(dependencies);
        if (MigrationStageSequence.IsHalting(dependencies.Status))
        {
            return Build(request, stages, EmptyRecommendation(dependencies), [], assumptions, blockers);
        }

        PlatformRecommendation recommendation = TargetPlatformAdvisor.Recommend(request.Evidence);
        blockers.AddRange(recommendation.Blockers);
        assumptions.AddRange(recommendation.Assumptions);

        StageResult advisory = RunAdvisory(recommendation);
        stages.Add(advisory);
        if (MigrationStageSequence.IsHalting(advisory.Status))
        {
            return Build(request, stages, recommendation, [], assumptions, blockers);
        }

        (StageResult planning, IReadOnlyList<ConversionTask> tasks) = RunConversionPlanning(request, recommendation);
        stages.Add(planning);
        foreach (string missing in planning.MissingEvidence)
        {
            blockers.Add($"Source artifact '{missing}' was not supplied; conversion artifacts cannot be generated for it.");
        }

        if (MigrationStageSequence.IsHalting(planning.Status))
        {
            return Build(request, stages, recommendation, tasks, assumptions, blockers);
        }

        StageResult review = RunValidationReview(stages, tasks);
        stages.Add(review);
        if (MigrationStageSequence.IsHalting(review.Status))
        {
            return Build(request, stages, recommendation, tasks, assumptions, blockers);
        }

        stages.Add(RunApprovalGate(request.Approval));
        return Build(request, stages, recommendation, tasks, assumptions, blockers);
    }

    private static StageResult RunIntake(MigrationAssessmentRequest request)
    {
        RequestValidationResult validation = RequestValidator.Validate(request);
        if (validation.IsValid)
        {
            return new StageResult(
                MigrationStage.Intake, FleetRole.Orchestrator, StageStatus.Completed,
                $"Accepted engagement '{request.EngagementId}' with {request.Evidence.Count} evidence item(s).",
                [], []);
        }

        return new StageResult(
            MigrationStage.Intake, FleetRole.Orchestrator, StageStatus.BlockedOnEvidence,
            "Request failed validation.",
            [.. validation.Errors.Select((error, index) => new Finding(
                FleetRole.Orchestrator, $"INTAKE-INVALID-{index + 1:D3}", "Invalid request", Severity.High, error, []))],
            []);
    }

    private static StageResult RunInventory(MigrationAssessmentRequest request)
    {
        FleetRoleDefinition role = FleetRoleCatalog.Get(FleetRole.InventoryAnalyst);
        IReadOnlyList<string> missing = MissingEvidence(request, role.RequiredEvidence, requireAll: true);
        if (missing.Count > 0)
        {
            return new StageResult(
                MigrationStage.InventoryAnalysis, role.Role, StageStatus.BlockedOnEvidence,
                "Cannot inventory the estate without a Forms module inventory.", [], missing);
        }

        List<Finding> findings =
        [
            new(role.Role, "INV-001", "Forms estate catalogued", Severity.Info,
                "Inventory established from the supplied module inventory artifacts.",
                [.. Cite(request, EvidenceKind.FormsModuleInventory)]),
        ];

        if (!request.Evidence.Any(e => e.Kind == EvidenceKind.FormsModuleSource && e.IsVerified))
        {
            findings.Add(new Finding(role.Role, "INV-002", "Module source not supplied", Severity.Medium,
                "Only inventory metadata was supplied; per-module conversion effort is an estimate.",
                [.. Cite(request, EvidenceKind.FormsModuleInventory)]));
        }

        EvidenceKind[] coverageGaps =
        [
            .. MigrationLandscapeCatalog.RecommendedExitEvidence
                .Where(kind => !request.Evidence.Any(item => item.Kind == kind && item.IsVerified)),
        ];
        if (coverageGaps.Length > 0)
        {
            findings.Add(new Finding(role.Role, "INV-003", "Legacy exit evidence is incomplete", Severity.Medium,
                "A module and schema inventory cannot establish business value or production readiness. " +
                "Collect or explicitly waive: " + string.Join(", ", coverageGaps) + ".",
                [.. Cite(request, EvidenceKind.FormsModuleInventory)]));
        }

        return new StageResult(
            MigrationStage.InventoryAnalysis, role.Role, StageStatus.Completed,
            "Inventory analysis complete.", findings, []);
    }

    private static StageResult RunDependencyMapping(MigrationAssessmentRequest request)
    {
        FleetRoleDefinition role = FleetRoleCatalog.Get(FleetRole.DependencyMapper);
        IReadOnlyList<string> missing = MissingEvidence(request, role.RequiredEvidence, requireAll: false);
        if (missing.Count > 0)
        {
            return new StageResult(
                MigrationStage.DependencyMapping, role.Role, StageStatus.BlockedOnEvidence,
                "Cannot map dependencies without a schema export or PL/SQL program units.", [], missing);
        }

        List<Finding> findings = [];
        foreach (IGrouping<WorkloadSignal, EvidenceItem> signalGroup in request.Evidence
                     .Where(item => item.IsVerified)
                     .SelectMany(item => item.Signals
                         .Where(signal => IsAuthoritativeSignalEvidence(signal, item.Kind))
                         .Select(signal => (Signal: signal, Item: item)))
                     .GroupBy(entry => entry.Signal, entry => entry.Item))
        {
            WorkloadSignal signal = signalGroup.Key;
            Severity severity = LegacyModernizationCatalog.Risks.TryGetValue(signal, out LegacyRiskDefinition? risk)
                ? risk.Severity
                : signal switch
                {
                    WorkloadSignal.FileSystemAccess => Severity.Critical,
                    WorkloadSignal.ExternalProcedureCalls => Severity.High,
                    _ when TargetPlatformAdvisor.HardManagedInstanceConstraints.ContainsKey(signal) => Severity.High,
                    _ => Severity.Low,
                };

            EvidenceItem[] sources = [.. signalGroup];
            findings.Add(new Finding(
                role.Role, $"DEP-{signal}", risk?.Title ?? $"Dependency: {signal}", severity,
                risk?.Detail ?? $"Signal '{signal}' was supplied by {sources.Length} evidence artifact(s).",
                [.. sources.Select(item => item.Id).Distinct(StringComparer.Ordinal)]));
        }

        if (findings.Count == 0)
        {
            findings.Add(new Finding(role.Role, "DEP-EVIDENCE-GAP", "No dependency signals supplied",
                Severity.Medium,
                "The supplied dependency artifacts contain no machine-readable signals; this is an evidence gap, not evidence that dependencies are absent.",
                [.. request.Evidence
                    .Where(item => item.Kind is EvidenceKind.DatabaseSchemaExport or EvidenceKind.PlSqlProgramUnit)
                    .Select(item => item.Id)]));
        }

        return new StageResult(
            MigrationStage.DependencyMapping, role.Role, StageStatus.Completed,
            $"Mapped {findings.Count} dependency finding(s).", findings, []);
    }

    private static StageResult RunAdvisory(PlatformRecommendation recommendation)
    {
        FleetRoleDefinition role = FleetRoleCatalog.Get(FleetRole.TargetPlatformAdvisor);

        if (recommendation.Recommended == TargetPlatform.Undetermined ||
            recommendation.Confidence == ConfidenceLevel.Insufficient ||
            recommendation.Blockers.Count > 0)
        {
            return new StageResult(
                MigrationStage.TargetPlatformAdvisory, role.Role, StageStatus.BlockedOnEvidence,
                "Target platform advisory is blocked by unresolved evidence or redesign requirements.",
                [new Finding(role.Role, "TPA-BLOCKED", "Target platform advisory blocked", Severity.High,
                    "A target cannot advance to conversion planning while recommendation blockers remain unresolved.",
                    [])],
                recommendation.Blockers);
        }

        return new StageResult(
            MigrationStage.TargetPlatformAdvisory, role.Role, StageStatus.Completed,
            $"Recommended {recommendation.Recommended} with {recommendation.Confidence} confidence.",
            [.. recommendation.Criteria.Select(c => new Finding(
                role.Role,
                $"TPA-{c.Criterion}",
                $"{(c.IsHardConstraint ? "Hard constraint" : "Indicator")}: {c.Criterion}",
                c.IsHardConstraint ? Severity.High : Severity.Low,
                c.Rationale,
                c.EvidenceIds))],
            []);
    }

    private static (StageResult Stage, IReadOnlyList<ConversionTask> Tasks) RunConversionPlanning(
        MigrationAssessmentRequest request, PlatformRecommendation recommendation)
    {
        FleetRoleDefinition role = FleetRoleCatalog.Get(FleetRole.ConversionPlanner);
        List<ConversionTask> tasks =
        [
            new("CT-UI", role.Role,
                "Replatform Oracle Forms UI to a supported application front end. No UI code is generated by this fleet.",
                Severity.High, [.. Cite(request, EvidenceKind.FormsModuleInventory)],
                MissingEvidence(request, [EvidenceKind.FormsModuleSource], requireAll: true)),
            new("CT-PLSQL", role.Role,
                $"Convert PL/SQL program units to T-SQL on {recommendation.Recommended}.",
                Severity.High, [.. Cite(request, EvidenceKind.PlSqlProgramUnit)],
                MissingEvidence(request, [EvidenceKind.PlSqlProgramUnit], requireAll: true)),
            new("CT-SCHEMA", role.Role,
                $"Assess, convert, and validate database schema and programmable objects on {recommendation.Recommended}.",
                Severity.High, [.. Cite(request, EvidenceKind.DatabaseSchemaExport)],
                MissingEvidence(request, [EvidenceKind.DatabaseSchemaExport], requireAll: true)),
            new("CT-DATA", role.Role,
                "Select the data-movement mode, rehearse migration, reconcile source and target, and prove rollback independently of schema conversion.",
                Severity.High, [.. Cite(request, EvidenceKind.DataProfile)],
                MissingEvidence(request, [EvidenceKind.DataProfile, EvidenceKind.CutoverAndRollbackPlan], requireAll: true)),
            new("CT-INTEGRATION-IDENTITY", role.Role,
                "Redesign and validate integration, authentication, authorization, and system-of-record boundaries.",
                Severity.High,
                [.. Cite(request, EvidenceKind.IntegrationInventory).Concat(Cite(request, EvidenceKind.AuthenticationTopology))],
                MissingEvidence(request, [EvidenceKind.IntegrationInventory, EvidenceKind.AuthenticationTopology], requireAll: true)),
            new("CT-DESIGN-RULES", role.Role,
                "Version global architecture decisions and form-level exceptions for navigation, transaction boundaries, locking, security, and client-versus-service logic before scaling automated conversion.",
                Severity.High,
                [.. Cite(request, EvidenceKind.BusinessProcessCatalog).Concat(Cite(request, EvidenceKind.FormsModuleSource))],
                MissingEvidence(request, [EvidenceKind.BusinessProcessCatalog, EvidenceKind.FormsModuleSource], requireAll: true)),
            new("CT-MIGRATION-WAVES", role.Role,
                "Cluster forms, shared libraries, reports, integrations, and database objects by dependency into independently testable migration waves with coexistence ownership and rollback.",
                Severity.High,
                [.. Cite(request, EvidenceKind.FormsModuleInventory).Concat(Cite(request, EvidenceKind.IntegrationInventory))],
                MissingEvidence(request, [EvidenceKind.FormsModuleInventory, EvidenceKind.IntegrationInventory, EvidenceKind.CutoverAndRollbackPlan], requireAll: true)),
            new("CT-CUTOVER", role.Role,
                "Define cutover, rollback, coexistence, and regression validation criteria with the business owner.",
                Severity.High, [.. Cite(request, EvidenceKind.CutoverAndRollbackPlan)],
                MissingEvidence(request, [EvidenceKind.CutoverAndRollbackPlan, EvidenceKind.TestBaseline], requireAll: true)),
            new("CT-PRODUCTION-READINESS", role.Role,
                "Obtain separate business, security, data, user, and operations acceptance after migration rehearsals; plan approval is not production acceptance.",
                Severity.High,
                [.. Cite(request, EvidenceKind.TestBaseline).Concat(Cite(request, EvidenceKind.LicensingAndSupportPosition))],
                MissingEvidence(request,
                    [EvidenceKind.BusinessProcessCatalog, EvidenceKind.TestBaseline, EvidenceKind.LicensingAndSupportPosition],
                    requireAll: true)),
        ];

        foreach (PlatformCriterionResult criterion in recommendation.Criteria.Where(c => c.IsHardConstraint))
        {
            tasks.Add(new ConversionTask(
                $"CT-{criterion.Criterion}", role.Role,
                $"Remediate '{criterion.Criterion}' during conversion. {criterion.Rationale}",
                Severity.High, criterion.EvidenceIds, []));
        }

        foreach (IGrouping<WorkloadSignal, EvidenceItem> riskGroup in request.Evidence
                     .Where(item => item.IsVerified)
                     .SelectMany(item => item.Signals
                         .Where(signal => LegacyModernizationCatalog.Risks.TryGetValue(signal, out LegacyRiskDefinition? risk) &&
                                          risk.PreferredEvidence.Contains(item.Kind))
                         .Select(signal => (Signal: signal, Item: item)))
                     .GroupBy(entry => entry.Signal, entry => entry.Item))
        {
            LegacyRiskDefinition risk = LegacyModernizationCatalog.Risks[riskGroup.Key];
            tasks.Add(new ConversionTask(
                risk.TaskId,
                role.Role,
                risk.TaskDescription,
                risk.Severity,
                [.. riskGroup.Select(item => item.Id).Distinct(StringComparer.Ordinal)],
                MissingEvidence(request, risk.PreferredEvidence, requireAll: true)));
        }

        IReadOnlyList<string> discoveryGaps =
            MissingEvidence(request, MigrationLandscapeCatalog.RecommendedExitEvidence, requireAll: true);
        if (discoveryGaps.Count > 0)
        {
            tasks.Add(new ConversionTask(
            "CT-DISCOVERY",
            role.Role,
            "Close or formally waive the cross-functional discovery gaps before selecting an irreversible exit strategy.",
            Severity.High,
            [],
            discoveryGaps));
        }

        EvidenceKind[] requiredSources =
            [EvidenceKind.FormsModuleSource, EvidenceKind.PlSqlProgramUnit, EvidenceKind.DatabaseSchemaExport];

        List<string> missingSources = [];
        foreach (EvidenceKind kind in requiredSources)
        {
            if (!request.Evidence.Any(e => e.Kind == kind && e.IsVerified))
            {
                missingSources.Add(kind.ToString());
            }
        }

        List<Finding> findings = [];
        if (missingSources.Count > 0)
        {
            findings.Add(new Finding(role.Role, "CP-NO-SOURCE", "Conversion artifacts cannot be generated",
                Severity.Medium,
                "Planning-only tasks were produced because the required source artifacts were not supplied: " +
                string.Join(", ", missingSources) + ".",
                []));
        }

        if (discoveryGaps.Count > 0)
        {
            findings.Add(new Finding(role.Role, "CP-DISCOVERY-GAPS", "Exit decision is not approval-ready",
                Severity.High,
                "Cross-functional discovery evidence is still required before a retire, replace, upgrade, wrap, strangler, or rewrite decision can be approved.",
                []));
        }

        IReadOnlyList<string> allMissing = [.. missingSources.Concat(discoveryGaps).Distinct(StringComparer.Ordinal)];
        StageStatus status = allMissing.Count == 0 ? StageStatus.Completed : StageStatus.BlockedOnEvidence;

        return (new StageResult(
            MigrationStage.ConversionPlanning, role.Role, status,
            status == StageStatus.Completed
                ? $"Produced {tasks.Count} conversion task(s)."
                : $"Produced {tasks.Count} planning task(s), but the plan is blocked on evidence.",
            findings, allMissing), tasks);
    }

    private static StageResult RunValidationReview(
        IReadOnlyList<StageResult> completedStages, IReadOnlyList<ConversionTask> tasks)
    {
        FleetRoleDefinition role = FleetRoleCatalog.Get(FleetRole.ValidationReviewer);
        IReadOnlyList<Finding> critical =
            [.. completedStages.SelectMany(s => s.Findings).Where(f => f.Severity == Severity.Critical)];

        if (critical.Count > 0)
        {
            return new StageResult(
                MigrationStage.ValidationReview, role.Role, StageStatus.Rejected,
                $"Rejected: {critical.Count} unresolved critical finding(s) must be remediated before approval.",
                [.. critical.Select(finding => finding with { Code = $"VR-{finding.Code}" })], []);
        }

        return new StageResult(
            MigrationStage.ValidationReview, role.Role, StageStatus.Completed,
            $"Reviewed {tasks.Count} conversion task(s); no unresolved critical findings.",
            [],
            []);
    }

    private static StageResult RunApprovalGate(HumanApproval approval)
    {
        FleetRoleDefinition role = FleetRoleCatalog.Get(FleetRole.Orchestrator);

        return approval.Decision switch
        {
            ApprovalDecision.Approved when !string.IsNullOrWhiteSpace(approval.ApproverId) => new StageResult(
                MigrationStage.HumanApproval, role.Role, StageStatus.Completed,
                $"Approved by '{approval.ApproverId}'.", [], []),

            ApprovalDecision.Approved => new StageResult(
                MigrationStage.HumanApproval, role.Role, StageStatus.BlockedOnApproval,
                "Approval decision supplied without an approver identity.",
                [new Finding(role.Role, "HA-NO-APPROVER", "Missing approver identity", Severity.High,
                    "An Approved decision requires ApproverId so the acceptance is auditable.", [])],
                []),

            ApprovalDecision.Rejected => new StageResult(
                MigrationStage.HumanApproval, role.Role, StageStatus.Rejected,
                $"Rejected by human reviewer. {approval.Notes ?? string.Empty}".TrimEnd(), [], []),

            _ => new StageResult(
                MigrationStage.HumanApproval, role.Role, StageStatus.BlockedOnApproval,
                "Awaiting human approval. Conversion artifacts remain unaccepted.", [], []),
        };
    }

    private static MigrationPlan Build(
        MigrationAssessmentRequest request,
        IReadOnlyList<StageResult> stages,
        PlatformRecommendation recommendation,
        IReadOnlyList<ConversionTask> tasks,
        IReadOnlyList<string> assumptions,
        IReadOnlyList<string> blockers)
    {
        StageResult last = stages[^1];
        List<string> effectiveBlockers = [.. blockers];
        if (MigrationStageSequence.IsHalting(last.Status))
        {
            IEnumerable<string> stageBlockers = last.MissingEvidence.Count > 0
                ? last.MissingEvidence.Select(missing => $"{last.Stage}: {missing}")
                : [last.Summary];
            effectiveBlockers.AddRange(stageBlockers);
        }

        effectiveBlockers = [.. effectiveBlockers.Distinct(StringComparer.Ordinal)];
        bool accepted = effectiveBlockers.Count == 0 &&
            last is { Stage: MigrationStage.HumanApproval, Status: StageStatus.Completed };

        return new MigrationPlan(
            request.EngagementId ?? string.Empty,
            request.ApplicationName ?? string.Empty,
            accepted ? MigrationStage.Completed : last.Stage,
            last.Status,
            accepted,
            recommendation,
            stages,
            tasks,
            assumptions,
            effectiveBlockers,
            FleetGuardrails.Disclaimers);
    }

    private static PlatformRecommendation EmptyRecommendation(StageResult haltedAt) =>
        new(TargetPlatform.Undetermined, ConfidenceLevel.Insufficient, [], [],
            [$"Platform advisory was not reached; the run halted at {haltedAt.Stage}."]);

    private static IReadOnlyList<string> MissingEvidence(
        MigrationAssessmentRequest request, IReadOnlyList<EvidenceKind> required, bool requireAll)
    {
        if (required.Count == 0)
        {
            return [];
        }

        List<string> missing = [.. required
            .Where(kind => !request.Evidence.Any(e => e.Kind == kind && e.IsVerified))
            .Select(kind => kind.ToString())];

        // requireAll=false means any one of the listed kinds satisfies the stage.
        return !requireAll && missing.Count < required.Count ? [] : missing;
    }

    private static IEnumerable<string> Cite(MigrationAssessmentRequest request, EvidenceKind kind) =>
        request.Evidence.Where(e => e.Kind == kind && e.IsVerified).Select(e => e.Id);

    private static bool IsAuthoritativeSignalEvidence(WorkloadSignal signal, EvidenceKind kind) =>
        LegacyModernizationCatalog.Risks.TryGetValue(signal, out LegacyRiskDefinition? risk)
            ? risk.PreferredEvidence.Contains(kind)
            : TargetPlatformAdvisor.IsAuthoritativeEvidence(signal, kind);
}
