// Copyright (c) Microsoft. All rights reserved.

using System.ComponentModel;

namespace OracleFormsMigrationFleet.Fleet;

/// <summary>Specialist roles coordinated by the orchestrator.</summary>
public enum FleetRole
{
    Orchestrator,
    InventoryAnalyst,
    DependencyMapper,
    TargetPlatformAdvisor,
    ConversionPlanner,
    ValidationReviewer,

    // Execution-lifecycle roles. They own phases in MigrationRunPlanner, not assessment stages.
    DocumentationAuthor,
    ApplicationCodeConverter,
    DatabaseConverter,
    BuildAndTestEngineer,
    DataMigrationEngineer,
    ReconciliationAnalyst,
    AcceptanceCoordinator,
}

/// <summary>Deterministic stages of the assessment pipeline, in execution order.</summary>
public enum MigrationStage
{
    Intake,
    InventoryAnalysis,
    DependencyMapping,
    TargetPlatformAdvisory,
    ConversionPlanning,
    ValidationReview,
    HumanApproval,
    Completed,

    /// <summary>Not part of the assessment pipeline. Marks roles that own <see cref="MigrationPhase"/> work.</summary>
    ExecutionLifecycle,
}

/// <summary>Outcome of a single stage.</summary>
public enum StageStatus
{
    NotStarted,
    Completed,
    BlockedOnEvidence,
    BlockedOnApproval,
    Rejected,
}

/// <summary>Categories of source artifact the fleet can reason over.</summary>
public enum EvidenceKind
{
    FormsModuleInventory,
    FormsModuleSource,
    FormsXmlExport,
    MenuModuleSource,
    SharedLibrarySource,
    ObjectLibrarySource,
    OracleReportsInventory,
    PlSqlProgramUnit,
    DatabaseSchemaExport,
    DatabaseLinkUsage,
    ExternalProcedureUsage,
    ScheduledJobInventory,
    IntegrationInventory,
    BusinessProcessCatalog,
    AuthenticationTopology,
    DataProfile,
    TestBaseline,
    CutoverAndRollbackPlan,
    LicensingAndSupportPosition,
    UsageAndBusinessValue,
    ReplacementProductFit,
    WorkloadProfile,
    ComplianceConstraint,
    NetworkTopology,
}

/// <summary>Machine-readable facts extracted from evidence, used for deterministic scoring.</summary>
public enum WorkloadSignal
{
    DatabaseLinksInUse,
    CrossDatabaseQueries,
    ScheduledDatabaseJobs,
    SqlAgentRequired,
    ClrOrExternalAssemblies,
    ClrRequiredInDatabase,
    DistributedTransactions,
    ServiceBrokerRequired,
    InstanceLevelCollationRequired,
    VnetIsolationRequired,
    LargeDatabaseFootprint,
    SelfContainedSchema,
    PerDatabaseElasticScale,
    ServerlessCostSensitivity,
    FileSystemAccess,
    ExternalProcedureCalls,
    ClientSidePlSql,
    FormsTriggerNavigationLogic,
    MultiRecordBlocks,
    EnterQueryMode,
    PostQueryLogic,
    SharedPllOrObjectLibraries,
    JavaBeansOrPjc,
    WebUtilOleOrJacob,
    HostCommandUsage,
    OracleReportsIntegration,
    DynamicSql,
    OracleAdvancedQueuing,
    VpdOrRowLevelSecurity,
    NlsSemanticDependency,
    EmptyStringNullDependency,
    PessimisticLocking,
    LegacyAuthentication,
    InaccessibleSource,
    MissingRegressionBaseline,
    OnlineCutoverRequired,
    CoexistenceRequired,
    LowOrNoBusinessUsage,
    PackageReplacementAvailable,
    RegulatoryRetentionOnly,
}

public enum Severity
{
    Info,
    Low,
    Medium,
    High,
    Critical,
}

public enum TargetPlatform
{
    Undetermined,
    AzureSqlDatabase,
    AzureSqlManagedInstance,
}

public enum ConfidenceLevel
{
    Insufficient,
    Low,
    Medium,
    High,
}

public enum ApprovalDecision
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>A single supplied artifact or attested fact about the source estate.</summary>
public sealed record EvidenceItem
{
    [Description("Stable identifier used to cite this evidence in findings and recommendations.")]
    public required string Id { get; init; }

    [Description("Category of the supplied artifact.")]
    public required EvidenceKind Kind { get; init; }

    [Description("Where the artifact came from, e.g. an export file name or ticket reference. Never a credential or connection string.")]
    public required string Source { get; init; }

    [Description("Short human-readable description of what the artifact shows.")]
    public required string Summary { get; init; }

    [Description("True when a human has confirmed the artifact came from the authoritative source system.")]
    public bool IsVerified { get; init; }

    [Description("Machine-readable facts extracted from the artifact.")]
    public IReadOnlyList<WorkloadSignal> Signals { get; init; } = [];
}

/// <summary>Human approval gate state. Conversion artifacts are never accepted without it.</summary>
public sealed record HumanApproval
{
    public ApprovalDecision Decision { get; init; } = ApprovalDecision.Pending;

    [Description("Identity of the approving human. Required for an Approved decision.")]
    public string? ApproverId { get; init; }

    public string? Notes { get; init; }

    public static HumanApproval Pending { get; } = new();
}

/// <summary>Inbound assessment request.</summary>
public sealed record MigrationAssessmentRequest
{
    [Description("Engagement or work-item identifier used for auditing.")]
    public required string EngagementId { get; init; }

    [Description("Name of the Oracle Forms application under assessment.")]
    public required string ApplicationName { get; init; }

    [Description("Oracle Forms version, or 'unknown' when not supplied.")]
    public string OracleFormsVersion { get; init; } = "unknown";

    [Description("Oracle Database version of the source estate, or 'unknown' when not supplied.")]
    public string OracleDatabaseVersion { get; init; } = "unknown";

    [Description("Supplied source artifacts. An empty list produces an evidence-blocked assessment.")]
    public IReadOnlyList<EvidenceItem> Evidence { get; init; } = [];

    [Description("Non-technical constraints such as regulatory or downtime limits.")]
    public IReadOnlyList<string> BusinessConstraints { get; init; } = [];

    [Description("Human approval gate state for the conversion plan.")]
    public HumanApproval Approval { get; init; } = HumanApproval.Pending;
}

public sealed record RequestValidationResult(bool IsValid, IReadOnlyList<string> Errors);

public sealed record Finding(
    FleetRole Role,
    string Code,
    string Title,
    Severity Severity,
    string Detail,
    IReadOnlyList<string> EvidenceIds);

public sealed record PlatformCriterionResult(
    string Criterion,
    TargetPlatform Indicates,
    bool IsHardConstraint,
    string Rationale,
    IReadOnlyList<string> EvidenceIds);

public sealed record PlatformRecommendation(
    TargetPlatform Recommended,
    ConfidenceLevel Confidence,
    IReadOnlyList<PlatformCriterionResult> Criteria,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Blockers);

public sealed record ConversionTask(
    string Id,
    FleetRole Owner,
    string Description,
    Severity Risk,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> Prerequisites);

public sealed record StageResult(
    MigrationStage Stage,
    FleetRole Role,
    StageStatus Status,
    string Summary,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<string> MissingEvidence);

/// <summary>Final auditable output of one orchestration run.</summary>
public sealed record MigrationPlan(
    string EngagementId,
    string ApplicationName,
    MigrationStage FinalStage,
    StageStatus FinalStatus,
    bool IsAccepted,
    PlatformRecommendation Recommendation,
    IReadOnlyList<StageResult> Stages,
    IReadOnlyList<ConversionTask> ConversionTasks,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Disclaimers);

/// <summary>Prompt and configuration definition for one specialist role.</summary>
public sealed record FleetRoleDefinition(
    FleetRole Role,
    MigrationStage Stage,
    string DisplayName,
    string Objective,
    string Instructions,
    IReadOnlyList<EvidenceKind> RequiredEvidence,
    IReadOnlyList<string> Guardrails);
