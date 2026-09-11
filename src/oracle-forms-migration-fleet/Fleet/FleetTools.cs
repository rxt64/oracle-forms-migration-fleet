// Copyright (c) Microsoft. All rights reserved.

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace OracleFormsMigrationFleet.Fleet;

/// <summary>
/// The deterministic fleet exposed to the model as tools. Every call is pure — no network,
/// no Azure dependency, no state mutation — so the model can only surface auditable results.
/// </summary>
public static class FleetTools
{
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IList<AITool> Create() =>
    [
        AIFunctionFactory.Create(
            AssessOracleFormsMigration,
            "assess_oracle_forms_migration",
            "Runs the deterministic Oracle Forms migration assessment pipeline over supplied evidence and " +
            "returns an auditable migration plan, including the Azure SQL Database vs Azure SQL Managed " +
            "Instance recommendation, stage statuses, blockers, and the human approval gate result.",
            SerializerOptions),

        AIFunctionFactory.Create(
            DescribeFleetRoles,
            "describe_fleet_roles",
            "Returns the specialist role definitions, the stage each one owns, and the evidence each one requires.",
            SerializerOptions),

        AIFunctionFactory.Create(
            DescribeEvidenceRequirements,
            "describe_evidence_requirements",
            "Returns which evidence kinds each stage requires and which workload signals drive the target platform decision.",
            SerializerOptions),

        AIFunctionFactory.Create(
            DescribeMigrationLandscape,
            "describe_migration_landscape",
            "Returns source-backed Oracle Forms and Azure migration tool boundaries, legacy exit strategies, deterministic risk signals, and mandatory human reviews.",
            SerializerOptions),

        AIFunctionFactory.Create(
            PlanOracleFormsMigrationRun,
            "plan_oracle_forms_migration_run",
            "Plans and coordinates the full artifact-producing migration lifecycle — source acquisition and analysis, " +
            "documentation, normalization, Forms-to-React/Java conversion, database conversion to PostgreSQL or the " +
            "SQL Server family, build and static validation, differential behavior testing, sandbox data migration, " +
            "reconciliation, human acceptance, and production cutover. It returns per-phase owners, required inputs, " +
            "expected workspace-relative output artifacts, tooling, mutation class, and gate state. This tool itself is " +
            "deterministic and offline: it starts no process, writes no file, and connects to no database, and the local " +
            "execution adapters it names are specified rather than implemented here. Nothing was generated or migrated " +
            "unless an execution adapter returned artifacts and a matching attestation.",
            SerializerOptions),
    ];

    [Description("Run the Oracle Forms migration assessment pipeline and return the resulting migration plan.")]
    public static MigrationPlan AssessOracleFormsMigration(
        [Description("The assessment request, including all supplied evidence and the human approval state.")]
        MigrationAssessmentRequest request) => MigrationFleetOrchestrator.Run(request);

    [Description("Plan the end-to-end Oracle Forms migration run. Planning only: no process is run and no database is contacted.")]
    public static MigrationRunPlan PlanOracleFormsMigrationRun(
        [Description("The run request, including the destination stack, workspace-relative source/output roots, evidence, approvals, and attestations.")]
        MigrationRunRequest request) => MigrationRunPlanner.Plan(request);

    [Description("Describe the specialist roles coordinated by this service.")]
    public static IReadOnlyList<FleetRoleDefinition> DescribeFleetRoles() =>
        [.. FleetRoleCatalog.All, .. FleetRoleCatalog.Execution];

    [Description("Describe the evidence each stage requires and the signals that drive the platform decision.")]
    public static EvidenceRequirements DescribeEvidenceRequirements() => new(
        [.. FleetRoleCatalog.All.Select(r => new StageEvidenceRequirement(r.Stage, r.Role, r.RequiredEvidence))],
        [.. TargetPlatformAdvisor.HardManagedInstanceConstraints.Select(
            kv => new SignalDescription(kv.Key, TargetPlatform.AzureSqlManagedInstance, true, kv.Value))],
        [.. TargetPlatformAdvisor.SoftIndicators.Select(
            kv => new SignalDescription(kv.Key, kv.Value.Platform, false, kv.Value.Rationale))],
        [.. TargetPlatformAdvisor.PlatformAgnosticBlockers.Select(
            kv => new SignalDescription(kv.Key, TargetPlatform.Undetermined, false, kv.Value))]);

    [Description("Describe available migration tools, viable legacy exit strategies, tool limitations, and required human reviews.")]
    public static MigrationLandscape DescribeMigrationLandscape() => new(
        MigrationLandscapeCatalog.Tools,
        MigrationLandscapeCatalog.ExitStrategies,
        [.. LegacyModernizationCatalog.Risks.Select(kv => new LegacyRiskDescription(
            kv.Key, kv.Value.Severity, kv.Value.Detail, kv.Value.PreferredEvidence))],
        MigrationLandscapeCatalog.MandatoryHumanReviews);
}

public sealed record StageEvidenceRequirement(
    MigrationStage Stage, FleetRole Role, IReadOnlyList<EvidenceKind> RequiredEvidence);

public sealed record SignalDescription(
    WorkloadSignal Signal, TargetPlatform Indicates, bool IsHardConstraint, string Rationale);

public sealed record EvidenceRequirements(
    IReadOnlyList<StageEvidenceRequirement> Stages,
    IReadOnlyList<SignalDescription> HardManagedInstanceConstraints,
    IReadOnlyList<SignalDescription> SoftIndicators,
    IReadOnlyList<SignalDescription> PlatformAgnosticBlockers);
