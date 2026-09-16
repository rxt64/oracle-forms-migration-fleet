// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;

namespace OracleFormsMigrationFleet.Tests;

public class FleetAttributionTests
{
    private static FleetAttribution Describe(string? review = "gpt-5.6-sol") =>
        FleetAttributionMap.Describe(
            [MigrationPhase.SourceAnalysis, MigrationPhase.DatabaseConversion],
            "gpt-5.4-mini",
            review);

    private static FleetAttribution DescribeRuntime() =>
        FleetAttributionMap.Describe(
            [
                MigrationPhase.SourceAnalysis,
                MigrationPhase.ApplicationCodeConversion,
                MigrationPhase.DatabaseConversion,
                MigrationPhase.SandboxDataMigration,
            ],
            "gpt-5.4-mini",
            "gpt-5.6-sol");

    private static PhaseAttribution Phase(FleetAttribution attribution, MigrationPhase phase) =>
        attribution.Phases.Single(candidate => candidate.Phase == phase);

    [Fact]
    public void Every_lifecycle_phase_is_attributed() =>
        Assert.Equal(
            MigrationRunPlanner.Lifecycle.Select(phase => phase.Phase),
            Describe().Phases.Select(phase => phase.Phase));

    [Fact]
    public void A_phase_without_an_adapter_is_reported_as_not_implemented()
    {
        PhaseAttribution phase = Phase(Describe(), MigrationPhase.ApplicationCodeConversion);

        Assert.Equal(PhaseEngine.NotImplemented, phase.Engine);
        Assert.Null(phase.ModelDeployment);
    }

    [Fact]
    public void Source_analysis_is_deterministic_and_names_no_model()
    {
        PhaseAttribution phase = Phase(Describe(), MigrationPhase.SourceAnalysis);

        Assert.Equal(PhaseEngine.Deterministic, phase.Engine);
        Assert.Null(phase.ModelDeployment);
    }

    [Fact]
    public void Database_conversion_names_the_review_deployment_when_one_is_configured()
    {
        PhaseAttribution phase = Phase(Describe(), MigrationPhase.DatabaseConversion);

        Assert.Equal(PhaseEngine.DeterministicWithModelReview, phase.Engine);
        Assert.Equal("gpt-5.6-sol", phase.ModelDeployment);
    }

    [Fact]
    public void Database_conversion_is_plain_deterministic_when_no_reviewer_is_configured()
    {
        PhaseAttribution phase = Phase(Describe(review: null), MigrationPhase.DatabaseConversion);

        Assert.Equal(PhaseEngine.Deterministic, phase.Engine);
        Assert.Null(phase.ModelDeployment);
    }

    [Fact]
    public void No_phase_is_attributed_wholly_to_a_model()
    {
        Assert.DoesNotContain(
            Describe().Phases,
            phase => phase.Engine is not (
                PhaseEngine.Deterministic
                or PhaseEngine.DeterministicWithModelReview
                or PhaseEngine.DeterministicWithBoundedModelRepair
                or PhaseEngine.NotImplemented));
    }

    [Fact]
    public void Exactly_one_phase_consults_a_model() =>
        Assert.Single(Describe().Phases, phase => phase.ModelDeployment is not null);

    [Fact]
    public void Runtime_attributes_every_reachable_review_and_repair_path()
    {
        FleetAttribution attribution = DescribeRuntime();

        Assert.Equal(
            PhaseEngine.DeterministicWithModelReview,
            Phase(attribution, MigrationPhase.ApplicationCodeConversion).Engine);
        Assert.Equal(
            PhaseEngine.DeterministicWithModelReview,
            Phase(attribution, MigrationPhase.DatabaseConversion).Engine);
        Assert.Equal(
            PhaseEngine.DeterministicWithBoundedModelRepair,
            Phase(attribution, MigrationPhase.SandboxDataMigration).Engine);
        Assert.All(
            new[]
            {
                MigrationPhase.ApplicationCodeConversion,
                MigrationPhase.DatabaseConversion,
                MigrationPhase.SandboxDataMigration,
            },
            phase => Assert.Equal("gpt-5.6-sol", Phase(attribution, phase).ModelDeployment));
    }

    [Fact]
    public void The_disclaimers_say_roles_are_not_independent_agents() =>
        Assert.Contains(
            Describe().Disclaimers,
            text => text.Contains("not independent agents", StringComparison.Ordinal));

    [Fact]
    public void Both_model_capabilities_are_listed_with_their_deployment()
    {
        FleetAttribution attribution = Describe();

        Assert.Equal(3, attribution.Models.Count);
        Assert.Contains(attribution.Models, model => model.Deployment == "gpt-5.4-mini");
        Assert.Contains(attribution.Models, model => model.Deployment == "gpt-5.6-sol");
    }
}

public class AzureFootprintTests
{
    [Theory]
    [InlineData(DatabaseTarget.PostgreSql, "Microsoft.DBforPostgreSQL/flexibleServers")]
    [InlineData(DatabaseTarget.AzureSqlDatabase, "Microsoft.Sql/servers + databases")]
    [InlineData(DatabaseTarget.AzureSqlManagedInstance, "Microsoft.Sql/managedInstances")]
    public void The_database_resource_follows_the_chosen_target(DatabaseTarget target, string expected) =>
        Assert.Contains(
            AzureFootprintCalculator.Describe(target, ExecutionMode.SandboxMigration).Resources,
            resource => resource.ResourceType == expected);

    [Fact]
    public void An_undetermined_target_names_no_database_resource()
    {
        AzureFootprint footprint = AzureFootprintCalculator.Describe(DatabaseTarget.Undetermined, ExecutionMode.PlanOnly);

        Assert.DoesNotContain(footprint.Resources, resource => resource.ResourceType.Contains("flexibleServers", StringComparison.Ordinal));
        Assert.Contains(footprint.Resources, resource => resource.ResourceType.Contains("not yet chosen", StringComparison.Ordinal));
    }

    [Fact]
    public void Contributor_is_never_requested_at_subscription_scope()
    {
        AzureFootprint footprint = AzureFootprintCalculator.Describe(DatabaseTarget.PostgreSql, ExecutionMode.ProductionCutover);

        AzureRoleRequirement contributor = footprint.Roles.Single(role => role.Role == "Contributor");
        Assert.DoesNotContain("Subscription", contributor.Scope, StringComparison.Ordinal);
        Assert.Contains("resource group", contributor.Scope, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_only_subscription_scoped_role_is_read_only()
    {
        AzureFootprint footprint = AzureFootprintCalculator.Describe(DatabaseTarget.PostgreSql, ExecutionMode.ProductionCutover);

        foreach (AzureRoleRequirement role in footprint.Roles.Where(role => role.Scope == "Subscription"))
        {
            Assert.Equal("Reader", role.Role);
        }
    }

    [Fact]
    public void The_footprint_states_that_nothing_here_can_be_created_by_this_build() =>
        Assert.Contains(
            AzureFootprintCalculator.Describe(DatabaseTarget.PostgreSql, ExecutionMode.SandboxMigration).Disclaimers,
            text => text.Contains("cannot create it", StringComparison.Ordinal));

    [Fact]
    public void The_tenant_model_requires_a_person_and_admin_consent()
    {
        IReadOnlyList<string> tenant = AzureFootprintCalculator
            .Describe(DatabaseTarget.PostgreSql, ExecutionMode.SandboxMigration).TenantModel;

        Assert.Contains(tenant, text => text.Contains("signed into by a person", StringComparison.Ordinal));
        Assert.Contains(tenant, text => text.Contains("consented to by an administrator", StringComparison.Ordinal));
        Assert.Contains(tenant, text => text.Contains("is not persisted", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_role_explains_why_it_is_needed() =>
        Assert.DoesNotContain(
            AzureFootprintCalculator.Describe(DatabaseTarget.PostgreSql, ExecutionMode.ProductionCutover).Roles,
            role => string.IsNullOrWhiteSpace(role.Why) || string.IsNullOrWhiteSpace(role.Scope));

    [Fact]
    public void Nothing_is_required_before_artifact_generation_except_model_inference()
    {
        AzureFootprint footprint = AzureFootprintCalculator.Describe(DatabaseTarget.PostgreSql, ExecutionMode.GenerateArtifacts);

        foreach (AzureRoleRequirement role in footprint.Roles.Where(role => role.NeededFrom == ExecutionMode.GenerateArtifacts))
        {
            Assert.Equal("Cognitive Services OpenAI User", role.Role);
        }
    }
}
