// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Fleet;

/// <summary>The six operator-facing macro steps, in execution order.</summary>
public enum WorkbenchStep
{
    AnalyzeSource,
    PlanModernization,
    ConvertApplication,
    TransformDatabase,
    MigrateAndValidate,
    CutOverDestination,
}

/// <summary>Honest state of an Azure component relative to what this repository actually wires up.</summary>
public enum AzureComponentState
{
    /// <summary>Part of the current running architecture, provable from this repository's code.</summary>
    Active,

    /// <summary>Supported by the current architecture but not configured in this environment.</summary>
    NotConfigured,

    /// <summary>Designed and planned only. No code in this repository connects to it.</summary>
    Planned,
}

/// <summary>One macro step of the operator workbench and the lifecycle phases it owns.</summary>
public sealed record WorkbenchMacroStep(
    WorkbenchStep Step,
    int Order,
    string Title,
    string Summary,
    IReadOnlyList<MigrationPhase> Phases,
    bool RequiresExecutionAdapter,
    bool AdapterConnected,
    IReadOnlyList<string> AzureComponentIds);

/// <summary>Readiness of a single Azure component, with the evidence behind the claim.</summary>
public sealed record AzureComponentStatus(
    string Id,
    string Name,
    string Service,
    AzureComponentState State,
    string Role,
    string Evidence);

/// <summary>One hop of the operational request path shown in the topology strip.</summary>
public sealed record AzureTopologyHop(
    int Order,
    string Name,
    string Detail,
    AzureComponentState State);

/// <summary>A database destination the GUI is allowed to offer. Azure destinations only.</summary>
public sealed record DatabaseTargetOption(
    DatabaseTarget Target,
    string Name,
    string Service,
    string Guidance);

/// <summary>An evidence toggle offered by the GUI.</summary>
public sealed record EvidenceKindOption(
    EvidenceKind Kind,
    string Name,
    bool RequiredForGeneration,
    string? AlternativeRequirement = null);

/// <summary>An execution mode offered by the GUI, and whether this repository can actually execute it.</summary>
public sealed record ExecutionModeOption(
    ExecutionMode Mode,
    string Name,
    string Description,
    bool ExecutableHere);

/// <summary>Everything the operator console needs to render before the first plan is requested.</summary>
public sealed record WorkbenchBootstrap(
    IReadOnlyList<WorkbenchMacroStep> Steps,
    IReadOnlyList<PhaseOwnership> Lifecycle,
    IReadOnlyList<EvidenceKindOption> EvidenceKinds,
    IReadOnlyList<DatabaseTargetOption> DatabaseTargets,
    IReadOnlyList<ExecutionModeOption> ExecutionModes,
    IReadOnlyList<AzureComponentStatus> AzureComponents,
    IReadOnlyList<AzureTopologyHop> Topology,
    FleetAttribution Attribution,
    bool AgentChatAvailable,
    string ExecutionBoundary,
    IReadOnlyList<string> Disclaimers);

/// <summary>Operator-facing state of a macro step, derived from a planned run.</summary>
public enum WorkbenchStepState
{
    /// <summary>Every phase this step owns is authorized by the plan.</summary>
    Ready,

    /// <summary>The first ready step in execution order.</summary>
    Current,

    /// <summary>At least one owned phase is blocked on evidence, approval, or attestation.</summary>
    Blocked,

    /// <summary>No owned phase was requested by the current execution mode.</summary>
    Planned,
}

/// <summary>Projection of one macro step against a concrete <see cref="MigrationRunPlan"/>.</summary>
public sealed record WorkbenchStepStatus(
    WorkbenchStep Step,
    int Order,
    string Title,
    WorkbenchStepState State,
    IReadOnlyList<MigrationPhase> Phases,
    int BlockedPhaseCount,
    bool RequiresExecutionAdapter,
    bool AdapterConnected,
    IReadOnlyList<string> Blockers);

/// <summary>Response of the plan endpoint: the real planner output plus the macro-step projection.</summary>
public sealed record WorkbenchPlanResponse(
    MigrationRunPlan Plan,
    IReadOnlyList<WorkbenchStepStatus> Steps,
    string ExecutionBoundary,
    AzureFootprint? AzureFootprint = null);

/// <summary>
/// Deterministic, request-independent description of the operator workbench. It groups the twelve
/// <see cref="MigrationPhase"/> values into six macro steps and states, honestly, which Azure
/// components this repository actually uses. It reads no environment variable, contacts no service,
/// and never carries a secret value: Application Insights readiness is supplied by the caller.
/// </summary>
public static class MigrationWorkbenchCatalog
{
    /// <summary>
    /// Whether a cutover can be performed here. No production adapter exists, so this stays false.
    /// This is a constant, not a probe: nothing in this codebase can flip it to true.
    /// </summary>
    public const bool ProductionAdapterConnected = false;

    public const string ExecutionBoundary =
        "This console plans and runs only planner-authorized adapters. Conversion and build phases write to the private session workspace. " +
        "When the host configures PostgreSQL, separately approved sandbox phases may write schema and data there. " +
        "Differential behavior testing, human acceptance, and production cutover are not executable here.";

    /// <summary>Evidence kinds the planner requires before any artifact may be generated.</summary>
    private static readonly EvidenceKind[] s_generationEvidence =
    [
        EvidenceKind.PlSqlProgramUnit,
        EvidenceKind.DatabaseSchemaExport,
        EvidenceKind.TestBaseline,
    ];

    public static IReadOnlyList<WorkbenchMacroStep> Steps { get; } =
    [
        new(WorkbenchStep.AnalyzeSource, 1,
            "Analyze source",
            "Collect the authoritative Forms, PL/SQL, and schema source and turn it into a dependency graph, inventory, and risk register.",
            [MigrationPhase.SourceAcquisition, MigrationPhase.SourceAnalysis],
            RequiresExecutionAdapter: false,
            AdapterConnected: true,
            ["foundry-hosted-agent", "app-insights"]),

        new(WorkbenchStep.PlanModernization, 2,
            "Plan modernization",
            "Document the as-is behavior and normalize the estate into a stable intermediate representation the converters can consume.",
            [MigrationPhase.DocumentationGeneration, MigrationPhase.SourceNormalization],
            RequiresExecutionAdapter: true,
            // SourceNormalization has an adapter, but it normalizes supplied Forms text and refuses a
            // binary-only estate rather than parsing modules. DocumentationGeneration still has none, so
            // the step as a whole is not connected.
            AdapterConnected: false,
            ["foundry-hosted-agent", "blob-storage"]),

        new(WorkbenchStep.ConvertApplication, 3,
            "Convert application",
            "Convert a reviewed pilot slice of Forms UI to React and client-side logic to Java/Spring Boot, then build and statically validate the output.",
            [MigrationPhase.ApplicationCodeConversion, MigrationPhase.BuildAndStaticValidation],
            RequiresExecutionAdapter: true,
            AdapterConnected: true,
            ["blob-storage", "app-insights"]),

        new(WorkbenchStep.TransformDatabase, 4,
            "Transform database",
            "Convert Oracle schema, types, and PL/SQL to the selected Azure database and record every unconvertible construct.",
            [MigrationPhase.DatabaseConversion],
            RequiresExecutionAdapter: true,
            AdapterConnected: true,
            ["key-vault", "database-target"]),

        new(WorkbenchStep.MigrateAndValidate, 5,
            "Migrate and validate",
            "Execute generated application tests, replay the regression baseline, load a representative data set into the sandbox, reconcile it against the source, read the migrated target back against every recorded decision, and deploy the verified application tier to its Azure destination.",
            [MigrationPhase.GeneratedApplicationVerification, MigrationPhase.DifferentialBehaviorTesting, MigrationPhase.SandboxDataMigration, MigrationPhase.DataReconciliation, MigrationPhase.TargetContractVerification, MigrationPhase.TargetApplicationDeployment],
            RequiresExecutionAdapter: true,
            // Generated application verification and sandbox migration run; differential testing has no adapter.
            AdapterConnected: true,
            ["managed-identity", "key-vault", "database-target"]),

        new(WorkbenchStep.CutOverDestination, 6,
            "Cut over destination",
            "Collect named acceptance sign-offs, then run the approved cutover runbook against the destination with rollback rehearsed.",
            [MigrationPhase.HumanAcceptance, MigrationPhase.ProductionCutover],
            RequiresExecutionAdapter: true,
            AdapterConnected: ProductionAdapterConnected,
            ["entra-id", "managed-identity", "database-target"]),
    ];

    /// <summary>Database destinations the GUI may offer. Azure destinations only.</summary>
    public static IReadOnlyList<DatabaseTargetOption> DatabaseTargets { get; } =
    [
        new(DatabaseTarget.AzureSqlDatabase, "Azure SQL Database", "Microsoft.Sql/servers/databases",
            "Self-contained schemas without SQL Agent, cross-database queries, or instance-level collation requirements."),
        new(DatabaseTarget.AzureSqlManagedInstance, "Azure SQL Managed Instance", "Microsoft.Sql/managedInstances",
            "Instance-scoped features: SQL Agent, cross-database queries, CLR, distributed transactions, or VNet isolation."),
        new(DatabaseTarget.PostgreSql, "Azure Database for PostgreSQL", "Microsoft.DBforPostgreSQL/flexibleServers",
            "Open-source destination converted with Ora2Pg; unconverted PL/SQL requires manual remediation."),
    ];

    public static IReadOnlyList<EvidenceKindOption> EvidenceKinds { get; } =
        [.. Enum.GetValues<EvidenceKind>()
            .Select(kind => new EvidenceKindOption(
                kind,
                Humanize(kind.ToString()),
                s_generationEvidence.Contains(kind),
                kind is EvidenceKind.FormsModuleSource or EvidenceKind.FormsXmlExport
                    ? "Provide either Forms module source or Forms XML export for artifact generation."
                    : null))];

    public static IReadOnlyList<ExecutionModeOption> ExecutionModes { get; } =
    [
        new(ExecutionMode.PlanOnly, "Plan only",
            "Produce the gated run plan. Nothing is written and nothing is executed.",
            ExecutableHere: true),
        new(ExecutionMode.GenerateArtifacts, "Generate artifacts",
            "Authorize workspace artifact writes once source, PL/SQL, schema, and baseline evidence are verified.",
            ExecutableHere: true),
        new(ExecutionMode.SandboxMigration, "Sandbox migration",
            "Authorize sandbox database writes. Requires a separate execution approval.",
            ExecutableHere: true),
        new(ExecutionMode.ProductionCutover, "Production cutover",
            "Authorize production writes. Requires a separate production approval plus independent attestations.",
            ExecutableHere: ProductionAdapterConnected),
    ];

    /// <summary>
    /// Azure component readiness. <paramref name="applicationInsightsConfigured"/> is supplied by the
    /// caller so this catalog stays pure and testable; it never reads or mutates process state.
    /// </summary>
    public static IReadOnlyList<AzureComponentStatus> AzureComponents(
        bool applicationInsightsConfigured,
        bool modelConfigured = true,
        bool agentChatAvailable = false,
        bool managedIdentityConfigured = false,
        bool entraAuthenticationConfigured = false,
        bool sandboxDatabaseConfigured = false) =>
    [
        new("foundry-hosted-agent", "Microsoft Foundry hosted agent", "Microsoft.CognitiveServices/accounts",
            modelConfigured || agentChatAvailable ? AzureComponentState.Active : AzureComponentState.NotConfigured,
            "Provides model-backed consultation for the deterministic fleet.",
            modelConfigured
                ? "AgentHost.CreateBuilder with AddFoundryResponses/MapFoundryResponses in Program.cs."
                : agentChatAvailable
                    ? "The backend proxy has an allowlisted FOUNDRY_AGENT_ENDPOINT and invokes it with managed identity."
                    : "Neither direct Azure OpenAI settings nor a hosted-agent endpoint are configured."),

        new("managed-identity", "Managed identity", "Microsoft.ManagedIdentity/userAssignedIdentities",
            managedIdentityConfigured ? AzureComponentState.Active : AzureComponentState.NotConfigured,
            "Authenticates the web backend to Azure without exposing a credential to the browser.",
            managedIdentityConfigured
                ? "AZURE_CLIENT_ID identifies the user-assigned workbench identity."
                : "AZURE_CLIENT_ID is absent, so no user-assigned workbench identity is configured."),

        new("app-insights", "Application Insights", "Microsoft.Insights/components",
            applicationInsightsConfigured ? AzureComponentState.Active : AzureComponentState.NotConfigured,
            "Receives OpenTelemetry traces and metrics emitted by the host.",
            applicationInsightsConfigured
                ? "APPLICATIONINSIGHTS_CONNECTION_STRING is present in this environment."
                : "APPLICATIONINSIGHTS_CONNECTION_STRING is not set, so traces are not exported."),

        new("entra-id", "Microsoft Entra ID", "Microsoft.Graph/applications",
            entraAuthenticationConfigured ? AzureComponentState.Active : AzureComponentState.NotConfigured,
            "Authenticates approved operators before Container Apps forwards requests to the workbench.",
            entraAuthenticationConfigured
                ? "WORKBENCH_ENTRA_AUTH_ENABLED is set by the Container Apps deployment with built-in authentication."
                : "Container Apps built-in authentication is not configured in this environment."),

        new("key-vault", "Azure Key Vault", "Microsoft.KeyVault/vaults",
            AzureComponentState.Planned,
            "Would hold source and destination database credentials used by execution adapters.",
            "No Key Vault client is referenced; this console never handles a secret value."),

        new("blob-storage", "Azure Blob Storage", "Microsoft.Storage/storageAccounts",
            AzureComponentState.Planned,
            "Would persist generated artifacts, conversion reports, and reconciliation output.",
            "Execution artifacts are currently written to an owner-scoped local session workspace; no Blob Storage client is connected."),

        new("database-target", "Selected Azure database destination", "Azure SQL Database, Azure SQL Managed Instance, or Azure Database for PostgreSQL",
            sandboxDatabaseConfigured ? AzureComponentState.Active : AzureComponentState.NotConfigured,
            "Destination for the converted schema, PL/SQL, and migrated data.",
            sandboxDatabaseConfigured
                ? "SANDBOX_PGHOST and SANDBOX_PGUSER bind the host-owned PostgreSQL gateway; callers cannot select another endpoint."
                : "No host-owned sandbox PostgreSQL target is configured in this environment."),

        new("execution-adapters", "Migration execution adapters", "Not an Azure resource",
            AzureComponentState.Active,
            "Perform source analysis, normalization, conversion, build validation, approved sandbox migration, and reconciliation.",
            "MigrationExecutor registers the implemented adapters. Differential behavior testing, human acceptance, and production cutover remain unimplemented."),
    ];

    /// <summary>Operational request path from the operator's browser to the selected destination.</summary>
    public static IReadOnlyList<AzureTopologyHop> Topology(
        bool applicationInsightsConfigured,
        bool modelConfigured = true,
        bool agentChatAvailable = false,
        bool managedIdentityConfigured = false,
        bool entraAuthenticationConfigured = false,
        bool sandboxDatabaseConfigured = false) =>
    [
        new(1, "Browser", "Operator console served from wwwroot by the hosted agent.", AzureComponentState.Active),
        new(2, "Microsoft Entra ID",
            entraAuthenticationConfigured ? "Container Apps authenticates an allowlisted operator before forwarding requests." : "Operator authentication is not configured locally.",
            entraAuthenticationConfigured ? AzureComponentState.Active : AzureComponentState.NotConfigured),
        new(3, "Foundry hosted workbench",
            modelConfigured
                ? "AgentHost process serving /, /api/workbench/*, and the model-backed /responses endpoint."
                : agentChatAvailable
                    ? "Container Apps serves the workbench and proxies consultation to the existing Foundry hosted agent."
                    : "AgentHost serves the local deterministic workbench without a model-backed consultation endpoint.",
            modelConfigured || agentChatAvailable ? AzureComponentState.Active : AzureComponentState.NotConfigured),
        new(4, "Managed identity",
            managedIdentityConfigured ? "A user-assigned identity authenticates outbound Foundry calls." : "No user-assigned workbench identity is configured.",
            managedIdentityConfigured ? AzureComponentState.Active : AzureComponentState.NotConfigured),
        new(5, "Key Vault / Blob Storage / App Insights",
            applicationInsightsConfigured
                ? "Application Insights is configured. Key Vault and Blob Storage are planned."
                : "Secrets, artifacts, and telemetry. None of the three is configured.",
            applicationInsightsConfigured ? AzureComponentState.Active : AzureComponentState.NotConfigured),
        new(6, "Sandbox PostgreSQL destination",
            sandboxDatabaseConfigured
                ? "The host has bound a PostgreSQL target for separately approved sandbox writes."
                : "No sandbox PostgreSQL target is configured in this environment.",
            sandboxDatabaseConfigured ? AzureComponentState.Active : AzureComponentState.NotConfigured),
    ];

    public static WorkbenchBootstrap Bootstrap(
        bool applicationInsightsConfigured,
        bool modelConfigured = true,
        bool agentChatAvailable = false,
        bool managedIdentityConfigured = false,
        bool entraAuthenticationConfigured = false,
        bool sandboxDatabaseConfigured = false,
        FleetAttribution? attribution = null) => new(
        Steps,
        MigrationRunPlanner.Lifecycle,
        EvidenceKinds,
        DatabaseTargets,
        ExecutionModes,
        AzureComponents(applicationInsightsConfigured, modelConfigured, agentChatAvailable, managedIdentityConfigured, entraAuthenticationConfigured, sandboxDatabaseConfigured),
        Topology(applicationInsightsConfigured, modelConfigured, agentChatAvailable, managedIdentityConfigured, entraAuthenticationConfigured, sandboxDatabaseConfigured),
        attribution ?? FleetAttributionMap.Describe([], null, null),
        agentChatAvailable,
        ExecutionBoundary,
        MigrationRunPlanner.Disclaimers);

    /// <summary>
    /// Derives macro-step state from a planned run. A step is blocked when any phase it owns is blocked,
    /// ready when any owned phase is authorized, and planned when the run did not request it. The first
    /// ready step in execution order is reported as current.
    /// </summary>
    public static IReadOnlyList<WorkbenchStepStatus> Project(MigrationRunPlan? plan)
    {
        IReadOnlyList<PhasePlan> phases = plan?.Phases ?? [];
        List<WorkbenchStepStatus> projected = [];
        bool currentAssigned = false;

        foreach (WorkbenchMacroStep step in Steps)
        {
            List<PhasePlan> owned = [.. phases.Where(phase => step.Phases.Contains(phase.Phase))];
            List<PhasePlan> blocked = [.. owned.Where(phase =>
                phase.Status is PhaseStatus.BlockedOnEvidence or PhaseStatus.BlockedOnApproval or PhaseStatus.BlockedOnAttestation)];

            WorkbenchStepState state;
            if (blocked.Count > 0)
            {
                state = WorkbenchStepState.Blocked;
            }
            else if (owned.Any(phase => phase.Status == PhaseStatus.Planned))
            {
                state = currentAssigned ? WorkbenchStepState.Ready : WorkbenchStepState.Current;
                currentAssigned = true;
            }
            else
            {
                state = WorkbenchStepState.Planned;
            }

            projected.Add(new WorkbenchStepStatus(
                step.Step,
                step.Order,
                step.Title,
                state,
                step.Phases,
                blocked.Count,
                step.RequiresExecutionAdapter,
                step.AdapterConnected,
                [.. blocked.SelectMany(phase => phase.Blockers.Select(b => $"{phase.Phase}: {b}")).Distinct(StringComparer.Ordinal)]));
        }

        return projected;
    }

    /// <summary>Splits a PascalCase enum name into spaced words for display.</summary>
    private static string Humanize(string name)
    {
        System.Text.StringBuilder builder = new(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(name[i]);
        }

        return builder.ToString();
    }
}
