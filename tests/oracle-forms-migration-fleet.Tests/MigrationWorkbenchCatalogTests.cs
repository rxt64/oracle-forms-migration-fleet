// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// The workbench catalog is pure: every test here supplies Application Insights readiness as an
/// argument, so no test reads or mutates a process environment variable.
/// </summary>
public class MigrationWorkbenchCatalogTests
{
    private static readonly string[] s_neverConnected =
        ["key-vault", "blob-storage", "database-target", "execution-adapters"];

    private static MigrationRunRequest Request(
        ExecutionMode mode,
        IReadOnlyList<EvidenceItem> evidence) => new()
        {
            EngagementId = "ENG-100",
            ApplicationName = "ORDERS",
            RequestedMode = mode,
            Target = new TargetStack { Database = DatabaseTarget.AzureSqlDatabase },
            SourceRoot = "legacy/forms",
            OutputRoot = "out/orders",
            Evidence = evidence,
        };

    private static IReadOnlyList<EvidenceItem> FullEvidence() =>
    [
        Requests.Evidence("EV-INV", EvidenceKind.FormsModuleInventory),
        Requests.Evidence("EV-SRC", EvidenceKind.FormsModuleSource),
        Requests.Evidence("EV-PLSQL", EvidenceKind.PlSqlProgramUnit),
        Requests.Evidence("EV-SCHEMA", EvidenceKind.DatabaseSchemaExport),
        Requests.Evidence("EV-TEST", EvidenceKind.TestBaseline),
        Requests.Evidence("EV-PROCESS", EvidenceKind.BusinessProcessCatalog),
        Requests.Evidence("EV-DATA", EvidenceKind.DataProfile),
        Requests.Evidence("EV-CUTOVER", EvidenceKind.CutoverAndRollbackPlan),
    ];

    private static WorkbenchStepStatus Step(IReadOnlyList<WorkbenchStepStatus> steps, WorkbenchStep step) =>
        steps.Single(s => s.Step == step);

    [Fact]
    public void Steps_are_the_six_macro_steps_in_execution_order()
    {
        IReadOnlyList<WorkbenchMacroStep> steps = MigrationWorkbenchCatalog.Steps;

        Assert.Equal(6, steps.Count);
        Assert.Equal(Enum.GetValues<WorkbenchStep>(), [.. steps.Select(s => s.Step)]);
        Assert.Equal([1, 2, 3, 4, 5, 6], [.. steps.Select(s => s.Order)]);
        Assert.All(steps, step =>
        {
            Assert.False(string.IsNullOrWhiteSpace(step.Title));
            Assert.False(string.IsNullOrWhiteSpace(step.Summary));
            Assert.NotEmpty(step.Phases);
        });
    }

    [Fact]
    public void Every_migration_phase_is_owned_by_exactly_one_step()
    {
        List<MigrationPhase> owned = [.. MigrationWorkbenchCatalog.Steps.SelectMany(step => step.Phases)];

        Assert.Equal(Enum.GetValues<MigrationPhase>().Length, owned.Count);
        Assert.Equal(owned.Count, owned.Distinct().Count());
        Assert.Empty(Enum.GetValues<MigrationPhase>().Except(owned));
    }

    [Fact]
    public void Each_step_orders_its_own_phases_by_the_planner_lifecycle()
    {
        List<MigrationPhase> lifecycle = [.. MigrationRunPlanner.Lifecycle.Select(phase => phase.Phase)];

        Assert.All(MigrationWorkbenchCatalog.Steps, step =>
        {
            List<int> positions = [.. step.Phases.Select(phase => lifecycle.IndexOf(phase))];
            Assert.DoesNotContain(-1, positions);
            Assert.Equal([.. positions.Order()], positions);
        });
    }

    [Fact]
    public void Database_targets_are_limited_to_the_three_azure_destinations()
    {
        DatabaseTarget[] offered = [.. MigrationWorkbenchCatalog.DatabaseTargets.Select(t => t.Target)];

        Assert.Equal(3, offered.Length);
        Assert.Contains(DatabaseTarget.AzureSqlDatabase, offered);
        Assert.Contains(DatabaseTarget.AzureSqlManagedInstance, offered);
        Assert.Contains(DatabaseTarget.PostgreSql, offered);
        Assert.DoesNotContain(DatabaseTarget.SqlServer, offered);
        Assert.DoesNotContain(DatabaseTarget.Undetermined, offered);
        Assert.All(MigrationWorkbenchCatalog.DatabaseTargets, option =>
            Assert.False(string.IsNullOrWhiteSpace(option.Service)));
    }

    [Fact]
    public void Database_target_names_match_the_strings_the_browser_branches_on()
    {
        // ClientApp/src/sourceClient.ts and ServiceGlyph.tsx compare the serialised target name to
        // pick the Azure resource abbreviation and the icon. Renaming the enum silently gives every
        // destination the Azure SQL Database naming, so the exact spellings are pinned here.
        string[] serialised = [.. MigrationWorkbenchCatalog.DatabaseTargets.Select(t => t.Target.ToString())];

        Assert.Contains("AzureSqlDatabase", serialised);
        Assert.Contains("AzureSqlManagedInstance", serialised);
        Assert.Contains("PostgreSql", serialised);
    }

    [Fact]
    public void Evidence_kind_options_cover_every_evidence_kind_and_mark_generation_prerequisites()
    {
        IReadOnlyList<EvidenceKindOption> options = MigrationWorkbenchCatalog.EvidenceKinds;

        Assert.Equal(Enum.GetValues<EvidenceKind>(), [.. options.Select(o => o.Kind)]);
        EvidenceKindOption formsSource = options.Single(o => o.Kind == EvidenceKind.FormsModuleSource);
        EvidenceKindOption formsXml = options.Single(o => o.Kind == EvidenceKind.FormsXmlExport);
        Assert.False(formsSource.RequiredForGeneration);
        Assert.False(formsXml.RequiredForGeneration);
        Assert.False(string.IsNullOrWhiteSpace(formsSource.AlternativeRequirement));
        Assert.Equal(formsSource.AlternativeRequirement, formsXml.AlternativeRequirement);
        Assert.True(options.Single(o => o.Kind == EvidenceKind.PlSqlProgramUnit).RequiredForGeneration);
        Assert.True(options.Single(o => o.Kind == EvidenceKind.TestBaseline).RequiredForGeneration);
        Assert.False(options.Single(o => o.Kind == EvidenceKind.NetworkTopology).RequiredForGeneration);
        Assert.Equal("Pl Sql Program Unit", options.Single(o => o.Kind == EvidenceKind.PlSqlProgramUnit).Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Only_proven_components_are_ever_reported_as_active(bool telemetryConfigured)
    {
        IReadOnlyList<AzureComponentStatus> components = MigrationWorkbenchCatalog.AzureComponents(telemetryConfigured);

        Assert.All(components.Where(c => s_neverConnected.Contains(c.Id)),
            component => Assert.Equal(AzureComponentState.Planned, component.State));

        Assert.Equal(AzureComponentState.Active, components.Single(c => c.Id == "foundry-hosted-agent").State);
        Assert.Equal(AzureComponentState.NotConfigured, components.Single(c => c.Id == "managed-identity").State);
        Assert.Equal(AzureComponentState.NotConfigured, components.Single(c => c.Id == "entra-id").State);
        Assert.All(components, component =>
        {
            Assert.False(string.IsNullOrWhiteSpace(component.Evidence));
            Assert.False(string.IsNullOrWhiteSpace(component.Role));
        });
    }

    [Theory]
    [InlineData(true, AzureComponentState.Active)]
    [InlineData(false, AzureComponentState.NotConfigured)]
    public void Application_insights_state_comes_only_from_the_supplied_flag(bool configured, AzureComponentState expected)
    {
        AzureComponentStatus telemetry = MigrationWorkbenchCatalog
            .AzureComponents(configured)
            .Single(c => c.Id == "app-insights");

        Assert.Equal(expected, telemetry.State);
        Assert.DoesNotContain("=", telemetry.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Offline_workbench_does_not_claim_the_model_agent_or_managed_identity_is_active()
    {
        IReadOnlyList<AzureComponentStatus> components = MigrationWorkbenchCatalog.AzureComponents(
            applicationInsightsConfigured: false,
            modelConfigured: false);

        Assert.Equal(AzureComponentState.NotConfigured,
            components.Single(c => c.Id == "foundry-hosted-agent").State);
        Assert.Equal(AzureComponentState.NotConfigured,
            components.Single(c => c.Id == "managed-identity").State);

        IReadOnlyList<AzureTopologyHop> topology = MigrationWorkbenchCatalog.Topology(
            applicationInsightsConfigured: false,
            modelConfigured: false);
        Assert.Equal(AzureComponentState.NotConfigured, topology.Single(h => h.Order == 3).State);
        Assert.Equal(AzureComponentState.NotConfigured, topology.Single(h => h.Order == 4).State);
    }

    [Fact]
    public void No_step_or_mode_claims_a_production_cutover_can_run_here()
    {
        Assert.False(MigrationWorkbenchCatalog.ProductionAdapterConnected);

        Assert.False(MigrationWorkbenchCatalog.Steps
            .Single(step => step.Step == WorkbenchStep.CutOverDestination).AdapterConnected);

        Assert.False(MigrationWorkbenchCatalog.ExecutionModes
            .Single(mode => mode.Mode == ExecutionMode.ProductionCutover).ExecutableHere);

        // Forms modules are never parsed here, so the step that depends on doing so must not claim otherwise.
        Assert.False(MigrationWorkbenchCatalog.Steps
            .Single(step => step.Step == WorkbenchStep.PlanModernization).AdapterConnected);

        Assert.True(MigrationWorkbenchCatalog.ExecutionModes
            .Single(mode => mode.Mode == ExecutionMode.PlanOnly).ExecutableHere);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Topology_is_ordered_and_never_claims_an_unconnected_hop_is_active(bool telemetryConfigured)
    {
        IReadOnlyList<AzureTopologyHop> hops = MigrationWorkbenchCatalog.Topology(telemetryConfigured);

        Assert.Equal(6, hops.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6], [.. hops.Select(h => h.Order)]);
        Assert.Equal(AzureComponentState.NotConfigured, hops.Single(h => h.Order == 2).State);
        Assert.Equal(AzureComponentState.NotConfigured, hops.Single(h => h.Order == 4).State);
        Assert.Equal(AzureComponentState.Planned, hops.Single(h => h.Order == 6).State);
        Assert.Equal(AzureComponentState.Planned, hops.Single(h => h.Order == 5).State);
    }

    [Fact]
    public void Bootstrap_projects_the_catalog_and_the_planner_lifecycle_and_disclaimers()
    {
        WorkbenchBootstrap bootstrap = MigrationWorkbenchCatalog.Bootstrap(applicationInsightsConfigured: false);

        Assert.Equal(MigrationWorkbenchCatalog.Steps, bootstrap.Steps);
        Assert.Equal(MigrationRunPlanner.Lifecycle, bootstrap.Lifecycle);
        Assert.Equal(MigrationRunPlanner.Disclaimers, bootstrap.Disclaimers);
        Assert.Equal(MigrationWorkbenchCatalog.ExecutionBoundary, bootstrap.ExecutionBoundary);
        Assert.False(bootstrap.AgentChatAvailable);
        Assert.Contains("No execution adapter is connected", bootstrap.ExecutionBoundary, StringComparison.Ordinal);
    }

    [Fact]
    public void Bootstrap_reports_agent_chat_only_when_the_host_proves_the_connection()
    {
        WorkbenchBootstrap bootstrap = MigrationWorkbenchCatalog.Bootstrap(
            applicationInsightsConfigured: true,
            modelConfigured: true,
            agentChatAvailable: true);

        Assert.True(bootstrap.AgentChatAvailable);
    }

    [Fact]
    public void Deployed_markers_activate_only_the_connected_identity_and_authentication_components()
    {
        WorkbenchBootstrap bootstrap = MigrationWorkbenchCatalog.Bootstrap(
            applicationInsightsConfigured: true,
            modelConfigured: false,
            agentChatAvailable: true,
            managedIdentityConfigured: true,
            entraAuthenticationConfigured: true);

        Assert.Equal(AzureComponentState.Active,
            bootstrap.AzureComponents.Single(c => c.Id == "foundry-hosted-agent").State);
        Assert.Equal(AzureComponentState.Active,
            bootstrap.AzureComponents.Single(c => c.Id == "managed-identity").State);
        Assert.Equal(AzureComponentState.Active,
            bootstrap.AzureComponents.Single(c => c.Id == "entra-id").State);
        Assert.All(bootstrap.AzureComponents.Where(c => s_neverConnected.Contains(c.Id)),
            component => Assert.Equal(AzureComponentState.Planned, component.State));
    }

    [Fact]
    public void Projection_marks_the_first_authorized_step_current_and_unrequested_steps_planned()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Request(ExecutionMode.PlanOnly, FullEvidence()));
        IReadOnlyList<WorkbenchStepStatus> steps = MigrationWorkbenchCatalog.Project(plan);

        Assert.Equal(6, steps.Count);
        Assert.Equal(WorkbenchStepState.Current, Step(steps, WorkbenchStep.AnalyzeSource).State);
        Assert.Equal(WorkbenchStepState.Planned, Step(steps, WorkbenchStep.ConvertApplication).State);
        Assert.Equal(WorkbenchStepState.Planned, Step(steps, WorkbenchStep.CutOverDestination).State);
        Assert.Single(steps, s => s.State == WorkbenchStepState.Current);
    }

    [Fact]
    public void Projection_marks_a_step_blocked_and_carries_its_phase_blockers()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Request(ExecutionMode.GenerateArtifacts, []));
        IReadOnlyList<WorkbenchStepStatus> steps = MigrationWorkbenchCatalog.Project(plan);

        WorkbenchStepStatus analyze = Step(steps, WorkbenchStep.AnalyzeSource);
        Assert.Equal(WorkbenchStepState.Blocked, analyze.State);
        Assert.Equal(2, analyze.BlockedPhaseCount);
        Assert.NotEmpty(analyze.Blockers);
        Assert.All(analyze.Blockers, blocker => Assert.Contains(":", blocker, StringComparison.Ordinal));
        Assert.DoesNotContain(steps, s => s.State == WorkbenchStepState.Current);
    }

    [Fact]
    public void Projection_reports_ready_after_the_current_step_when_generation_is_authorized()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Request(ExecutionMode.GenerateArtifacts, FullEvidence()));
        IReadOnlyList<WorkbenchStepStatus> steps = MigrationWorkbenchCatalog.Project(plan);

        Assert.Equal(ExecutionMode.GenerateArtifacts, plan.AuthorizedMode);
        Assert.Equal(WorkbenchStepState.Current, Step(steps, WorkbenchStep.AnalyzeSource).State);
        Assert.Equal(WorkbenchStepState.Ready, Step(steps, WorkbenchStep.ConvertApplication).State);
        Assert.Equal(WorkbenchStepState.Ready, Step(steps, WorkbenchStep.TransformDatabase).State);
        Assert.Equal(WorkbenchStepState.Planned, Step(steps, WorkbenchStep.MigrateAndValidate).State);
    }

    [Fact]
    public void Projection_of_no_plan_returns_all_six_steps_as_planned()
    {
        IReadOnlyList<WorkbenchStepStatus> steps = MigrationWorkbenchCatalog.Project(null);

        Assert.Equal(6, steps.Count);
        Assert.All(steps, step =>
        {
            Assert.Equal(WorkbenchStepState.Planned, step.State);
            Assert.Equal(0, step.BlockedPhaseCount);
            Assert.Empty(step.Blockers);
        });
    }

    [Fact]
    public void Projection_preserves_adapter_state_so_the_gui_cannot_offer_a_cutover()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Request(ExecutionMode.GenerateArtifacts, FullEvidence()));

        // The projection must carry the catalog's answer through rather than inferring one from the plan,
        // so a step the GUI paints as runnable is one an adapter will actually pick up.
        var projected = MigrationWorkbenchCatalog.Project(plan);

        Assert.False(projected.Single(step => step.Step == WorkbenchStep.CutOverDestination).AdapterConnected);
        Assert.False(projected.Single(step => step.Step == WorkbenchStep.PlanModernization).AdapterConnected);
        Assert.True(projected.Single(step => step.Step == WorkbenchStep.TransformDatabase).AdapterConnected);
    }
}
