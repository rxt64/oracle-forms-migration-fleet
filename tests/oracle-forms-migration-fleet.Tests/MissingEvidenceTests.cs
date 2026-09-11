// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;

namespace OracleFormsMigrationFleet.Tests;

public class MissingEvidenceTests
{
    [Fact]
    public void Empty_evidence_blocks_at_inventory_analysis()
    {
        MigrationPlan plan = MigrationFleetOrchestrator.Run(Requests.Build());

        Assert.Equal(MigrationStage.InventoryAnalysis, plan.FinalStage);
        Assert.Equal(StageStatus.BlockedOnEvidence, plan.FinalStatus);
        Assert.Contains(nameof(EvidenceKind.FormsModuleInventory), plan.Stages[^1].MissingEvidence);
    }

    [Fact]
    public void Dependency_mapping_accepts_either_schema_export_or_plsql()
    {
        MigrationPlan withPlsqlOnly = MigrationFleetOrchestrator.Run(Requests.Build(
        [
            Requests.Evidence("EV-INV", EvidenceKind.FormsModuleInventory),
            Requests.Evidence("EV-PLSQL", EvidenceKind.PlSqlProgramUnit),
        ]));

        StageResult mapping = Assert.Single(
            withPlsqlOnly.Stages, s => s.Stage == MigrationStage.DependencyMapping);
        Assert.Equal(StageStatus.Completed, mapping.Status);
    }

    [Fact]
    public void Undetermined_platform_blocks_before_conversion_planning()
    {
        MigrationPlan plan = MigrationFleetOrchestrator.Run(Requests.Build(Requests.CompleteEvidence()));

        Assert.Equal(MigrationStage.TargetPlatformAdvisory, plan.FinalStage);
        Assert.Equal(StageStatus.BlockedOnEvidence, plan.FinalStatus);
        Assert.Equal(TargetPlatform.Undetermined, plan.Recommendation.Recommended);
        Assert.Empty(plan.ConversionTasks);
    }

    [Fact]
    public void Missing_source_artifacts_produce_planning_only_tasks_and_blockers()
    {
        MigrationPlan plan = MigrationFleetOrchestrator.Run(Requests.Build(
        [
            Requests.Evidence("EV-INV", EvidenceKind.FormsModuleInventory),
            Requests.Evidence("EV-SCHEMA", EvidenceKind.DatabaseSchemaExport),
            Requests.Evidence("EV-PROFILE", EvidenceKind.WorkloadProfile, true, WorkloadSignal.SelfContainedSchema),
        ],
            Requests.Approved()));

        Assert.False(plan.IsAccepted);
        Assert.Equal(MigrationStage.ConversionPlanning, plan.FinalStage);
        Assert.Equal(StageStatus.BlockedOnEvidence, plan.FinalStatus);
        Assert.DoesNotContain(plan.Stages, stage => stage.Stage == MigrationStage.HumanApproval);
        Assert.NotEmpty(plan.ConversionTasks);
        Assert.Contains(plan.Blockers, b => b.Contains(nameof(EvidenceKind.FormsModuleSource), StringComparison.Ordinal));
        Assert.Contains(plan.Blockers, b => b.Contains("cannot be generated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Full_source_evidence_leaves_no_missing_source_blockers()
    {
        MigrationPlan plan = MigrationFleetOrchestrator.Run(Requests.Build(
            Requests.CompleteEvidence(WorkloadSignal.SelfContainedSchema), Requests.Approved()));

        Assert.DoesNotContain(plan.Blockers, b => b.Contains("cannot be generated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validation_review_does_not_claim_every_task_has_evidence()
    {
        MigrationPlan plan = MigrationFleetOrchestrator.Run(Requests.Build(
            Requests.CompleteEvidence(WorkloadSignal.SelfContainedSchema), Requests.Approved()));

        StageResult review = Assert.Single(plan.Stages, s => s.Stage == MigrationStage.ValidationReview);
        Assert.DoesNotContain(review.Findings, f =>
            f.Detail.Contains("every conversion task is traceable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Halted_run_reports_that_advisory_was_never_reached()
    {
        MigrationPlan plan = MigrationFleetOrchestrator.Run(Requests.Build());

        Assert.Contains(plan.Recommendation.Blockers, b => b.Contains("not reached", StringComparison.OrdinalIgnoreCase));
    }
}
