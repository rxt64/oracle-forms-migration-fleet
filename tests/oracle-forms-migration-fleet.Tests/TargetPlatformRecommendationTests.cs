// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;

namespace OracleFormsMigrationFleet.Tests;

public class TargetPlatformRecommendationTests
{
    [Fact]
    public void No_signals_yields_undetermined_with_insufficient_confidence()
    {
        PlatformRecommendation recommendation = TargetPlatformAdvisor.Recommend(
            [Requests.Evidence("EV-INV", EvidenceKind.FormsModuleInventory)]);

        Assert.Equal(TargetPlatform.Undetermined, recommendation.Recommended);
        Assert.Equal(ConfidenceLevel.Insufficient, recommendation.Confidence);
        Assert.NotEmpty(recommendation.Blockers);
    }

    [Theory]
    [InlineData(WorkloadSignal.SqlAgentRequired)]
    [InlineData(WorkloadSignal.ClrRequiredInDatabase)]
    [InlineData(WorkloadSignal.ServiceBrokerRequired)]
    [InlineData(WorkloadSignal.InstanceLevelCollationRequired)]
    public void Hard_constraint_forces_managed_instance_with_high_confidence(WorkloadSignal signal)
    {
        PlatformRecommendation recommendation =
            TargetPlatformAdvisor.Recommend(Requests.CompleteEvidence(signal));

        Assert.Equal(TargetPlatform.AzureSqlManagedInstance, recommendation.Recommended);
        Assert.Equal(ConfidenceLevel.High, recommendation.Confidence);
        Assert.Contains(recommendation.Criteria, c => c.IsHardConstraint && c.Criterion == signal.ToString());
    }

    [Theory]
    [InlineData(WorkloadSignal.ScheduledDatabaseJobs)]
    [InlineData(WorkloadSignal.ClrOrExternalAssemblies)]
    public void Observed_redesignable_features_are_only_managed_instance_indicators(WorkloadSignal signal)
    {
        PlatformRecommendation recommendation =
            TargetPlatformAdvisor.Recommend(Requests.CompleteEvidence(signal));

        Assert.Equal(TargetPlatform.AzureSqlManagedInstance, recommendation.Recommended);
        Assert.Equal(ConfidenceLevel.Medium, recommendation.Confidence);
        Assert.Contains(recommendation.Criteria, c => !c.IsHardConstraint && c.Criterion == signal.ToString());
    }

    [Fact]
    public void Hard_constraint_overrides_sql_database_indicators_and_records_the_assumption()
    {
        PlatformRecommendation recommendation = TargetPlatformAdvisor.Recommend(Requests.CompleteEvidence(
            WorkloadSignal.SqlAgentRequired,
            WorkloadSignal.SelfContainedSchema,
            WorkloadSignal.ServerlessCostSensitivity));

        Assert.Equal(TargetPlatform.AzureSqlManagedInstance, recommendation.Recommended);
        Assert.Contains(recommendation.Assumptions, a => a.Contains("overridden", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(WorkloadSignal.CrossDatabaseQueries)]
    [InlineData(WorkloadSignal.DistributedTransactions)]
    public void Compatibility_sensitive_features_are_managed_instance_indicators(WorkloadSignal signal)
    {
        PlatformRecommendation recommendation =
            TargetPlatformAdvisor.Recommend(Requests.CompleteEvidence(signal));

        Assert.Equal(TargetPlatform.AzureSqlManagedInstance, recommendation.Recommended);
        Assert.Equal(ConfidenceLevel.Medium, recommendation.Confidence);
        Assert.Contains(recommendation.Criteria, c => !c.IsHardConstraint && c.Criterion == signal.ToString());
    }

    [Fact]
    public void Oracle_database_links_require_redesign_instead_of_forcing_managed_instance()
    {
        PlatformRecommendation recommendation =
            TargetPlatformAdvisor.Recommend(
            [
                Requests.Evidence("EV-DBLINK", EvidenceKind.DatabaseLinkUsage, true,
                    WorkloadSignal.DatabaseLinksInUse),
            ]);

        Assert.Equal(TargetPlatform.Undetermined, recommendation.Recommended);
        Assert.Contains(recommendation.Blockers, b => b.Contains("do not map directly", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Soft_indicators_only_yield_sql_database_at_medium_confidence()
    {
        PlatformRecommendation recommendation = TargetPlatformAdvisor.Recommend(Requests.CompleteEvidence(
            WorkloadSignal.SelfContainedSchema, WorkloadSignal.ServerlessCostSensitivity));

        Assert.Equal(TargetPlatform.AzureSqlDatabase, recommendation.Recommended);
        Assert.Equal(ConfidenceLevel.Medium, recommendation.Confidence);
        Assert.All(recommendation.Criteria, c => Assert.False(c.IsHardConstraint));
    }

    [Fact]
    public void Evenly_split_soft_indicators_stay_undetermined()
    {
        PlatformRecommendation recommendation = TargetPlatformAdvisor.Recommend(Requests.CompleteEvidence(
            WorkloadSignal.SelfContainedSchema, WorkloadSignal.VnetIsolationRequired));

        Assert.Equal(TargetPlatform.Undetermined, recommendation.Recommended);
        Assert.Equal(ConfidenceLevel.Low, recommendation.Confidence);
        Assert.Contains(recommendation.Blockers, b => b.Contains("evenly split", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unverified_evidence_is_ignored_for_platform_selection()
    {
        PlatformRecommendation recommendation = TargetPlatformAdvisor.Recommend(
        [
            Requests.Evidence("EV-PROFILE", EvidenceKind.WorkloadProfile, verified: false,
                WorkloadSignal.ScheduledDatabaseJobs),
        ]);

        Assert.Equal(TargetPlatform.Undetermined, recommendation.Recommended);
        Assert.Equal(ConfidenceLevel.Insufficient, recommendation.Confidence);
        Assert.Contains(recommendation.Assumptions, a => a.Contains("not verified", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_workload_profile_caps_confidence_and_records_a_blocker()
    {
        PlatformRecommendation recommendation = TargetPlatformAdvisor.Recommend(
        [
            Requests.Evidence("EV-SCHEMA", EvidenceKind.DatabaseSchemaExport, true,
                WorkloadSignal.SelfContainedSchema),
        ]);

        Assert.Equal(TargetPlatform.AzureSqlDatabase, recommendation.Recommended);
        Assert.Equal(ConfidenceLevel.Low, recommendation.Confidence);
        Assert.Contains(recommendation.Blockers, b => b.Contains(nameof(EvidenceKind.WorkloadProfile), StringComparison.Ordinal));
    }

    [Fact]
    public void Platform_agnostic_signals_are_reported_as_blockers()
    {
        PlatformRecommendation recommendation =
            TargetPlatformAdvisor.Recommend(
            [
                Requests.Evidence("EV-PROFILE", EvidenceKind.WorkloadProfile, true,
                    WorkloadSignal.SelfContainedSchema),
                Requests.Evidence("EV-EXTPROC", EvidenceKind.ExternalProcedureUsage, true,
                    WorkloadSignal.ExternalProcedureCalls),
            ]);

        Assert.Contains(recommendation.Blockers, b => b.Contains("re-hosted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_criterion_cites_the_evidence_it_came_from()
    {
        PlatformRecommendation recommendation =
            TargetPlatformAdvisor.Recommend(Requests.CompleteEvidence(WorkloadSignal.CrossDatabaseQueries));

        Assert.All(recommendation.Criteria, c => Assert.NotEmpty(c.EvidenceIds));
    }

    [Fact]
    public void Platform_signal_from_unrelated_evidence_is_ignored()
    {
        PlatformRecommendation recommendation = TargetPlatformAdvisor.Recommend(
        [
            Requests.Evidence("EV-INV", EvidenceKind.FormsModuleInventory, true,
                WorkloadSignal.SqlAgentRequired),
            Requests.Evidence("EV-PROFILE", EvidenceKind.WorkloadProfile),
        ]);

        Assert.Equal(TargetPlatform.Undetermined, recommendation.Recommended);
        Assert.DoesNotContain(recommendation.Criteria, c => c.Criterion == nameof(WorkloadSignal.SqlAgentRequired));
        Assert.Contains(recommendation.Assumptions, a => a.Contains("Ignored signal", StringComparison.Ordinal));
    }

    [Fact]
    public void Repeated_signals_have_unique_evidence_citations()
    {
        PlatformRecommendation recommendation = TargetPlatformAdvisor.Recommend(
        [
            Requests.Evidence("EV-PROFILE", EvidenceKind.WorkloadProfile, true,
                WorkloadSignal.SelfContainedSchema, WorkloadSignal.SelfContainedSchema),
        ]);

        PlatformCriterionResult criterion = Assert.Single(recommendation.Criteria);
        Assert.Equal(["EV-PROFILE"], criterion.EvidenceIds);
    }
}
