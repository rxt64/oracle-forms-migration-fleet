// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;

namespace OracleFormsMigrationFleet.Tests;

public class StageProgressionTests
{
    [Fact]
    public void Stage_sequence_is_ordered_and_terminates()
    {
        Assert.Equal(MigrationStage.InventoryAnalysis, MigrationStageSequence.Next(MigrationStage.Intake));
        Assert.Equal(MigrationStage.HumanApproval, MigrationStageSequence.Next(MigrationStage.ValidationReview));
        Assert.Null(MigrationStageSequence.Next(MigrationStage.Completed));
    }

    [Theory]
    [InlineData(StageStatus.Completed, false)]
    [InlineData(StageStatus.NotStarted, false)]
    [InlineData(StageStatus.BlockedOnEvidence, true)]
    [InlineData(StageStatus.BlockedOnApproval, true)]
    [InlineData(StageStatus.Rejected, true)]
    public void Halting_statuses_are_classified(StageStatus status, bool expected) =>
        Assert.Equal(expected, MigrationStageSequence.IsHalting(status));

    [Fact]
    public void Invalid_request_halts_at_intake()
    {
        MigrationPlan plan = MigrationFleetOrchestrator.Run(
            Requests.Build(Requests.CompleteEvidence(WorkloadSignal.SelfContainedSchema), engagementId: "  "));

        StageResult only = Assert.Single(plan.Stages);
        Assert.Equal(MigrationStage.Intake, only.Stage);
        Assert.Equal(StageStatus.BlockedOnEvidence, only.Status);
        Assert.False(plan.IsAccepted);
    }

    [Fact]
    public void Complete_evidence_walks_every_stage_in_order()
    {
        MigrationPlan plan = MigrationFleetOrchestrator.Run(
            Requests.Build(Requests.CompleteEvidence(WorkloadSignal.SelfContainedSchema), Requests.Approved()));

        Assert.Equal(
            [
                MigrationStage.Intake,
                MigrationStage.InventoryAnalysis,
                MigrationStage.DependencyMapping,
                MigrationStage.TargetPlatformAdvisory,
                MigrationStage.ConversionPlanning,
                MigrationStage.ValidationReview,
                MigrationStage.HumanApproval,
            ],
            plan.Stages.Select(s => s.Stage));
        Assert.All(plan.Stages, s => Assert.Equal(StageStatus.Completed, s.Status));
        Assert.Equal(MigrationStage.Completed, plan.FinalStage);
    }

    [Fact]
    public void Stages_after_a_halt_are_never_executed()
    {
        MigrationPlan plan = MigrationFleetOrchestrator.Run(Requests.Build(
        [
            Requests.Evidence("EV-INV", EvidenceKind.FormsModuleInventory),
        ]));

        Assert.Equal(MigrationStage.DependencyMapping, plan.FinalStage);
        Assert.Equal(StageStatus.BlockedOnEvidence, plan.FinalStatus);
        Assert.DoesNotContain(plan.Stages, s => s.Stage == MigrationStage.TargetPlatformAdvisory);
        Assert.Empty(plan.ConversionTasks);
    }

    [Fact]
    public void Authoritative_file_system_access_blocks_at_platform_advisory()
    {
        IReadOnlyList<EvidenceItem> evidence =
        [
            .. Requests.CompleteEvidence(WorkloadSignal.SelfContainedSchema),
            Requests.Evidence("EV-FS", EvidenceKind.FormsModuleSource, true,
                WorkloadSignal.FileSystemAccess),
        ];
        MigrationPlan plan = MigrationFleetOrchestrator.Run(Requests.Build(evidence, Requests.Approved()));

        Assert.Equal(MigrationStage.TargetPlatformAdvisory, plan.FinalStage);
        Assert.Equal(StageStatus.BlockedOnEvidence, plan.FinalStatus);
        Assert.False(plan.IsAccepted);
        Assert.DoesNotContain(plan.Stages, s => s.Stage == MigrationStage.HumanApproval);
        Assert.NotEmpty(plan.Blockers);
    }

    [Fact]
    public void Forms_runtime_risk_produces_cited_finding_and_remediation_task()
    {
        IReadOnlyList<EvidenceItem> evidence =
        [
            .. Requests.CompleteEvidence(WorkloadSignal.SelfContainedSchema),
            Requests.Evidence("EV-FORMS", EvidenceKind.FormsXmlExport, true,
                WorkloadSignal.FormsTriggerNavigationLogic),
        ];

        MigrationPlan plan = MigrationFleetOrchestrator.Run(
            Requests.Build(evidence, Requests.Approved()));

        Finding finding = Assert.Single(
            plan.Stages.Single(stage => stage.Stage == MigrationStage.DependencyMapping).Findings,
            finding => finding.Code == "DEP-FormsTriggerNavigationLogic");
        Assert.Equal(Severity.High, finding.Severity);
        Assert.Equal(["EV-FORMS"], finding.EvidenceIds);

        ConversionTask task = Assert.Single(
            plan.ConversionTasks, task => task.Id == "CT-FORMS-SEMANTICS");
        Assert.Equal(["EV-FORMS"], task.EvidenceIds);
        Assert.DoesNotContain(nameof(EvidenceKind.BusinessProcessCatalog), task.Prerequisites);
        Assert.True(plan.IsAccepted);
    }

    [Theory]
    [InlineData(WorkloadSignal.MultiRecordBlocks, "CT-EDITABLE-GRIDS")]
    [InlineData(WorkloadSignal.EnterQueryMode, "CT-QUERY-BEHAVIOR")]
    [InlineData(WorkloadSignal.PostQueryLogic, "CT-POST-QUERY")]
    public void Interactive_forms_semantics_produce_cited_design_tasks(
        WorkloadSignal signal, string expectedTaskId)
    {
        IReadOnlyList<EvidenceItem> evidence =
        [
            .. Requests.CompleteEvidence(WorkloadSignal.SelfContainedSchema),
            Requests.Evidence("EV-INTERACTION", EvidenceKind.FormsModuleSource, true, signal),
        ];

        MigrationPlan plan = MigrationFleetOrchestrator.Run(
            Requests.Build(evidence, Requests.Approved()));

        Finding finding = Assert.Single(
            plan.Stages.Single(stage => stage.Stage == MigrationStage.DependencyMapping).Findings,
            finding => finding.Code == $"DEP-{signal}");
        Assert.Equal(Severity.High, finding.Severity);
        Assert.Equal(["EV-INTERACTION"], finding.EvidenceIds);

        ConversionTask task = Assert.Single(plan.ConversionTasks, task => task.Id == expectedTaskId);
        Assert.Equal(["EV-INTERACTION"], task.EvidenceIds);
    }

    [Fact]
    public void Interactive_forms_estate_produces_a_coherent_accepted_plan()
    {
        IReadOnlyList<EvidenceItem> evidence =
        [
            .. Requests.CompleteEvidence(WorkloadSignal.SelfContainedSchema),
            Requests.Evidence("EV-XML", EvidenceKind.FormsXmlExport),
            Requests.Evidence("EV-INTERACTION", EvidenceKind.FormsModuleSource, true,
                WorkloadSignal.MultiRecordBlocks,
                WorkloadSignal.EnterQueryMode,
                WorkloadSignal.PostQueryLogic),
        ];

        MigrationPlan plan = MigrationFleetOrchestrator.Run(
            Requests.Build(evidence, Requests.Approved()));

        Assert.True(plan.IsAccepted);
        Assert.Equal(MigrationStage.Completed, plan.FinalStage);
        Assert.Equal(StageStatus.Completed, plan.FinalStatus);
        Assert.Empty(plan.Blockers);
        Assert.Equal(TargetPlatform.AzureSqlDatabase, plan.Recommendation.Recommended);
        Assert.Equal(ConfidenceLevel.Medium, plan.Recommendation.Confidence);
        Assert.Empty(plan.Recommendation.Blockers);
        PlatformCriterionResult criterion = Assert.Single(
            plan.Recommendation.Criteria,
            criterion => criterion.Criterion == nameof(WorkloadSignal.SelfContainedSchema));
        Assert.Equal(TargetPlatform.AzureSqlDatabase, criterion.Indicates);
        Assert.False(criterion.IsHardConstraint);
        Assert.Equal(["EV-PROFILE"], criterion.EvidenceIds);

        string[] expectedTaskIds =
        [
            "CT-EDITABLE-GRIDS",
            "CT-QUERY-BEHAVIOR",
            "CT-POST-QUERY",
            "CT-DESIGN-RULES",
            "CT-MIGRATION-WAVES",
        ];
        foreach (string taskId in expectedTaskIds)
        {
            ConversionTask task = Assert.Single(plan.ConversionTasks, task => task.Id == taskId);
            Assert.Equal(Severity.High, task.Risk);
            Assert.Empty(task.Prerequisites);
            Assert.NotEmpty(task.EvidenceIds);
        }
        Assert.Equal(["EV-INTERACTION"],
            plan.ConversionTasks.Single(task => task.Id == "CT-EDITABLE-GRIDS").EvidenceIds);
        Assert.Equal(["EV-INTERACTION"],
            plan.ConversionTasks.Single(task => task.Id == "CT-QUERY-BEHAVIOR").EvidenceIds);
        Assert.Equal(["EV-INTERACTION"],
            plan.ConversionTasks.Single(task => task.Id == "CT-POST-QUERY").EvidenceIds);
        Assert.Equal(["EV-PROCESS", "EV-SRC", "EV-INTERACTION"],
            plan.ConversionTasks.Single(task => task.Id == "CT-DESIGN-RULES").EvidenceIds);
        Assert.Equal(["EV-INV", "EV-INTEGRATION"],
            plan.ConversionTasks.Single(task => task.Id == "CT-MIGRATION-WAVES").EvidenceIds);

        Assert.Contains(
            plan.Disclaimers,
            disclaimer => disclaimer.Contains(
                "No migration, schema change, or data movement was performed.",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(WorkloadSignal.WebUtilOleOrJacob, "CT-DESKTOP-INTEGRATION")]
    [InlineData(WorkloadSignal.HostCommandUsage, "CT-HOST-COMMANDS")]
    [InlineData(WorkloadSignal.VpdOrRowLevelSecurity, "CT-VPD")]
    [InlineData(WorkloadSignal.InaccessibleSource, "CT-SOURCE-RECOVERY")]
    public void Critical_legacy_exit_risks_are_rejected(
        WorkloadSignal signal, string expectedTaskId)
    {
        LegacyRiskDefinition risk = LegacyModernizationCatalog.Risks[signal];
        IReadOnlyList<EvidenceItem> evidence =
        [
            .. Requests.CompleteEvidence(WorkloadSignal.SelfContainedSchema),
            Requests.Evidence("EV-RISK", risk.PreferredEvidence[0], true, signal),
        ];
        MigrationPlan plan = MigrationFleetOrchestrator.Run(Requests.Build(evidence, Requests.Approved()));

        Assert.Equal(MigrationStage.ValidationReview, plan.FinalStage);
        Assert.Equal(StageStatus.Rejected, plan.FinalStatus);
        Assert.Contains(plan.ConversionTasks, task => task.Id == expectedTaskId);
        Assert.Contains(
            plan.Stages.Single(stage => stage.Stage == MigrationStage.ValidationReview).Findings,
            finding => finding.Code == $"VR-DEP-{signal}" && finding.Severity == Severity.Critical);
    }

    [Fact]
    public void Plan_always_carries_non_execution_disclaimers()
    {
        MigrationPlan plan = MigrationFleetOrchestrator.Run(Requests.Build());
        Assert.NotEmpty(plan.Disclaimers);
        Assert.Contains(plan.Disclaimers, d => d.Contains("No migration", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_cross_functional_evidence_is_visible_and_actionable()
    {
        MigrationPlan plan = MigrationFleetOrchestrator.Run(Requests.Build(
        [
            Requests.Evidence("EV-INV", EvidenceKind.FormsModuleInventory),
            Requests.Evidence("EV-SRC", EvidenceKind.FormsModuleSource),
            Requests.Evidence("EV-PLSQL", EvidenceKind.PlSqlProgramUnit),
            Requests.Evidence("EV-SCHEMA", EvidenceKind.DatabaseSchemaExport),
            Requests.Evidence("EV-PROFILE", EvidenceKind.WorkloadProfile, true,
                WorkloadSignal.SelfContainedSchema),
        ]));

        Finding gap = Assert.Single(
            plan.Stages.Single(stage => stage.Stage == MigrationStage.InventoryAnalysis).Findings,
            finding => finding.Code == "INV-003");
        Assert.Contains(nameof(EvidenceKind.BusinessProcessCatalog), gap.Detail);
        Assert.Contains(nameof(EvidenceKind.TestBaseline), gap.Detail);

        ConversionTask discovery = Assert.Single(plan.ConversionTasks, task => task.Id == "CT-DISCOVERY");
        Assert.Contains(nameof(EvidenceKind.IntegrationInventory), discovery.Prerequisites);
        Assert.Contains(nameof(EvidenceKind.LicensingAndSupportPosition), discovery.Prerequisites);
        Assert.Equal(MigrationStage.ConversionPlanning, plan.FinalStage);
        Assert.Equal(StageStatus.BlockedOnEvidence, plan.FinalStatus);
    }

    [Fact]
    public void Workstreams_are_separate_and_prerequisites_only_show_unmet_evidence()
    {
        MigrationPlan plan = MigrationFleetOrchestrator.Run(
            Requests.Build(Requests.CompleteEvidence(WorkloadSignal.SelfContainedSchema), Requests.Approved()));

        Assert.Contains(plan.ConversionTasks, task => task.Id == "CT-UI");
        Assert.Contains(plan.ConversionTasks, task => task.Id == "CT-SCHEMA");
        Assert.Contains(plan.ConversionTasks, task => task.Id == "CT-DATA");
        Assert.Contains(plan.ConversionTasks, task => task.Id == "CT-INTEGRATION-IDENTITY");
        Assert.Contains(plan.ConversionTasks, task => task.Id == "CT-PRODUCTION-READINESS");
        Assert.All(plan.ConversionTasks, task => Assert.Empty(task.Prerequisites));
    }

    [Fact]
    public void Repeated_signals_produce_one_dependency_finding_with_all_citations()
    {
        IReadOnlyList<EvidenceItem> evidence =
        [
            .. Requests.CompleteEvidence(WorkloadSignal.SelfContainedSchema),
            Requests.Evidence("EV-PROFILE-2", EvidenceKind.WorkloadProfile, true,
                WorkloadSignal.SelfContainedSchema),
        ];

        MigrationPlan plan = MigrationFleetOrchestrator.Run(Requests.Build(evidence));
        Finding finding = Assert.Single(
            plan.Stages.Single(stage => stage.Stage == MigrationStage.DependencyMapping).Findings,
            finding => finding.Code == "DEP-SelfContainedSchema");

        Assert.Equal(["EV-PROFILE", "EV-PROFILE-2"], finding.EvidenceIds);
    }

    [Fact]
    public void Empty_signal_set_is_reported_as_an_evidence_gap_not_absence()
    {
        MigrationPlan plan = MigrationFleetOrchestrator.Run(Requests.Build(
        [
            Requests.Evidence("EV-INV", EvidenceKind.FormsModuleInventory),
            Requests.Evidence("EV-SCHEMA", EvidenceKind.DatabaseSchemaExport),
        ]));

        Finding finding = Assert.Single(
            plan.Stages.Single(stage => stage.Stage == MigrationStage.DependencyMapping).Findings);
        Assert.Equal("DEP-EVIDENCE-GAP", finding.Code);
        Assert.Contains("not evidence", finding.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["EV-SCHEMA"], finding.EvidenceIds);
    }

    [Fact]
    public void Generated_task_ids_and_finding_codes_are_globally_unique()
    {
        IReadOnlyList<EvidenceItem> evidence =
        [
            .. Requests.CompleteEvidence(WorkloadSignal.SelfContainedSchema),
            Requests.Evidence("EV-FORMS-RISK", EvidenceKind.FormsModuleSource, true,
                WorkloadSignal.FormsTriggerNavigationLogic),
            Requests.Evidence("EV-REPORT-RISK", EvidenceKind.OracleReportsInventory, true,
                WorkloadSignal.OracleReportsIntegration),
        ];
        MigrationPlan plan = MigrationFleetOrchestrator.Run(Requests.Build(evidence, Requests.Approved()));

        Assert.Equal(plan.ConversionTasks.Count, plan.ConversionTasks.Select(task => task.Id).Distinct().Count());
        string[] findingCodes = [.. plan.Stages.SelectMany(stage => stage.Findings).Select(finding => finding.Code)];
        Assert.Equal(findingCodes.Length, findingCodes.Distinct().Count());
    }

    [Fact]
    public void Unverified_evidence_cannot_satisfy_gates_or_reach_approval()
    {
        IReadOnlyList<EvidenceItem> evidence =
        [.. Requests.CompleteEvidence(WorkloadSignal.SelfContainedSchema)
            .Select(item => item with { IsVerified = false })];

        MigrationPlan plan = MigrationFleetOrchestrator.Run(
            Requests.Build(evidence, Requests.Approved()));

        Assert.False(plan.IsAccepted);
        Assert.Equal(MigrationStage.InventoryAnalysis, plan.FinalStage);
        Assert.Equal(StageStatus.BlockedOnEvidence, plan.FinalStatus);
        Assert.DoesNotContain(plan.Stages, stage => stage.Stage == MigrationStage.HumanApproval);
    }

    [Fact]
    public void Recommendation_blocker_halts_before_approval()
    {
        IReadOnlyList<EvidenceItem> evidence =
        [
            .. Requests.CompleteEvidence(WorkloadSignal.SelfContainedSchema),
            Requests.Evidence("EV-EXTPROC", EvidenceKind.ExternalProcedureUsage, true,
                WorkloadSignal.ExternalProcedureCalls),
        ];

        MigrationPlan plan = MigrationFleetOrchestrator.Run(
            Requests.Build(evidence, Requests.Approved()));

        Assert.False(plan.IsAccepted);
        Assert.Equal(MigrationStage.TargetPlatformAdvisory, plan.FinalStage);
        Assert.Equal(StageStatus.BlockedOnEvidence, plan.FinalStatus);
        Assert.NotEmpty(plan.Recommendation.Blockers);
        Assert.DoesNotContain(plan.Stages, stage => stage.Stage == MigrationStage.HumanApproval);
    }

    [Fact]
    public void Legacy_risk_signal_from_unrelated_evidence_is_ignored()
    {
        IReadOnlyList<EvidenceItem> evidence =
        [
            .. Requests.CompleteEvidence(WorkloadSignal.SelfContainedSchema),
            Requests.Evidence("EV-WRONG", EvidenceKind.WorkloadProfile, true,
                WorkloadSignal.WebUtilOleOrJacob),
        ];

        MigrationPlan plan = MigrationFleetOrchestrator.Run(
            Requests.Build(evidence, Requests.Approved()));

        Assert.True(plan.IsAccepted);
        Assert.DoesNotContain(plan.Stages.SelectMany(stage => stage.Findings),
            finding => finding.Code.Contains(nameof(WorkloadSignal.WebUtilOleOrJacob), StringComparison.Ordinal));
        Assert.DoesNotContain(plan.ConversionTasks, task => task.Id == "CT-DESKTOP-INTEGRATION");
    }

    [Fact]
    public void File_system_signal_from_unrelated_evidence_is_ignored()
    {
        IReadOnlyList<EvidenceItem> evidence =
        [
            .. Requests.CompleteEvidence(WorkloadSignal.SelfContainedSchema),
            Requests.Evidence("EV-WRONG", EvidenceKind.WorkloadProfile, true,
                WorkloadSignal.FileSystemAccess),
        ];

        MigrationPlan plan = MigrationFleetOrchestrator.Run(
            Requests.Build(evidence, Requests.Approved()));

        Assert.True(plan.IsAccepted);
        Assert.DoesNotContain(plan.Stages.SelectMany(stage => stage.Findings),
            finding => finding.Code.Contains(nameof(WorkloadSignal.FileSystemAccess), StringComparison.Ordinal));
    }

    [Fact]
    public void Catalog_task_ids_do_not_collide_with_base_or_hard_constraint_tasks()
    {
        string[] baseTaskIds =
        [
            "CT-UI", "CT-PLSQL", "CT-SCHEMA", "CT-DATA", "CT-INTEGRATION-IDENTITY",
            "CT-DESIGN-RULES", "CT-MIGRATION-WAVES", "CT-CUTOVER",
            "CT-PRODUCTION-READINESS", "CT-DISCOVERY",
        ];
        string[] allTaskIds =
        [
            .. baseTaskIds,
            .. LegacyModernizationCatalog.Risks.Values.Select(risk => risk.TaskId),
            .. TargetPlatformAdvisor.HardManagedInstanceConstraints.Keys.Select(signal => $"CT-{signal}"),
        ];

        Assert.Equal(allTaskIds.Length, allTaskIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Unverified_inventory_artifacts_are_reported_as_missing()
    {
        IReadOnlyList<EvidenceItem> evidence =
        [
            Requests.Evidence("EV-INV", EvidenceKind.FormsModuleInventory),
            Requests.Evidence("EV-SRC", EvidenceKind.FormsModuleSource, verified: false),
            Requests.Evidence("EV-SCHEMA", EvidenceKind.DatabaseSchemaExport),
            Requests.Evidence("EV-PROCESS", EvidenceKind.BusinessProcessCatalog, verified: false),
        ];

        MigrationPlan plan = MigrationFleetOrchestrator.Run(Requests.Build(evidence));
        StageResult inventory = plan.Stages.Single(stage => stage.Stage == MigrationStage.InventoryAnalysis);

        Assert.Contains(inventory.Findings, finding => finding.Code == "INV-002");
        Finding discoveryGap = Assert.Single(inventory.Findings, finding => finding.Code == "INV-003");
        Assert.Contains(nameof(EvidenceKind.BusinessProcessCatalog), discoveryGap.Detail);
    }
}
