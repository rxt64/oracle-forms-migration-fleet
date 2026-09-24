// Copyright (c) Microsoft. All rights reserved.

using System.ComponentModel;

namespace OracleFormsMigrationFleet.Fleet;

/// <summary>Front-end framework the fleet converts Oracle Forms UI into.</summary>
public enum FrontEndStack
{
    React,
}

/// <summary>
/// Back-end framework the fleet converts Oracle Forms client-side logic into.
///
/// Values are appended, never reordered: a stack is persisted in run reports and stored plans, so an old
/// report has to keep decoding as the stack it was written with. <see cref="JavaSpringBoot"/> stays the
/// default for that reason too — a run that never stated a stack is still the Java run it was planned as.
/// </summary>
public enum BackEndStack
{
    JavaSpringBoot,

    /// <summary>ASP.NET Core on .NET 10 with Npgsql, generated from a validated target-mapping manifest.</summary>
    AspNetCore,
}

/// <summary>Database engine the Oracle schema, PL/SQL, and data are converted to.</summary>
public enum DatabaseTarget
{
    Undetermined,
    PostgreSql,
    SqlServer,
    AzureSqlDatabase,
    AzureSqlManagedInstance,
}

/// <summary>
/// How far a run is authorized to go. Values are ordered; a higher mode implies every lower one.
/// </summary>
public enum ExecutionMode
{
    PlanOnly,
    GenerateArtifacts,
    SandboxMigration,
    ProductionCutover,
}

/// <summary>
/// End-to-end lifecycle phases.
///
/// The numbers are explicit and frozen, because a phase is persisted in run reports, manifests, and
/// stored plans. <see cref="SourceNormalization"/> was added after the rest and took a new value at the
/// end rather than being inserted in lifecycle position, which would have renumbered every phase after
/// it and made an old report decode as a different phase.
///
/// Declaration order is therefore NOT execution order. <c>MigrationLifecycle.Order</c> is the single
/// place execution order is stated; nothing may sort phases by their numeric value.
/// </summary>
public enum MigrationPhase
{
    SourceAcquisition = 0,
    SourceAnalysis = 1,
    DocumentationGeneration = 2,
    ApplicationCodeConversion = 3,
    DatabaseConversion = 4,
    BuildAndStaticValidation = 5,
    DifferentialBehaviorTesting = 6,
    SandboxDataMigration = 7,
    DataReconciliation = 8,
    HumanAcceptance = 9,
    ProductionCutover = 10,

    /// <summary>Added after the values above were already in persisted output; runs before conversion.</summary>
    SourceNormalization = 11,

    /// <summary>Executes tests against generated application output without asserting source equivalence.</summary>
    GeneratedApplicationVerification = 12,

    /// <summary>
    /// Publishes the verified generated application tier to its Azure target through a trusted builder.
    ///
    /// It is a sandbox-class mutation, not a cutover: it puts this run's generated output in front of the
    /// converted database so the migrated application can be exercised, and it says nothing about the
    /// customer's production estate, which <see cref="ProductionCutover"/> still governs.
    /// </summary>
    TargetApplicationDeployment = 13,

    /// <summary>
    /// Reads the approved target after the migration landed in it, and records what each recorded
    /// disposition actually got.
    ///
    /// It is strictly read-only and runs after <see cref="SandboxDataMigration"/>, because until then
    /// there is no migrated database to read. It is the only phase whose results are attributable to one
    /// recorded decision each; <see cref="GeneratedApplicationVerification"/> above it runs generated test
    /// suites and reports aggregates, which say how many cases ran and not which decision any was about.
    /// </summary>
    TargetContractVerification = 14,
}

/// <summary>What a phase is allowed to change. Drives which approval gate applies.</summary>
public enum MutationClass
{
    None,
    WorkspaceArtifactWrite,
    SandboxDatabaseWrite,
    ProductionWrite,

    /// <summary>
    /// Opens a connection to the customer's approved target and reads it, writing only its own report
    /// into the session workspace.
    ///
    /// It changes nothing in the target, but it reaches a customer database, so it is held to the same
    /// just-in-time grant check a write is instead of being counted as a workspace artifact write. The
    /// earlier labelling said <see cref="WorkspaceArtifactWrite"/>, which was true about where the bytes
    /// landed and false about what the phase touched: the executor's gate never fired and a revoked or
    /// expired grant did not stop the connection.
    /// </summary>
    ExternalTargetRead,
}

/// <summary>Planning outcome for a single phase.</summary>
public enum PhaseStatus
{
    Planned,
    BlockedOnEvidence,
    BlockedOnApproval,
    BlockedOnAttestation,
    NotRequested,
}

/// <summary>Categories of artifact a phase consumes or produces.</summary>
public enum ArtifactKind
{
    SourceInput,
    Documentation,
    NormalizedSource,
    FrontEndCode,
    BackEndCode,
    DatabaseSchema,
    DataMigrationScript,
    TestSuite,
    ValidationReport,
    ExecutableVerificationReport,
    ReconciliationReport,
    CutoverRunbook,
}

/// <summary>Independently signed facts that unlock the sandbox and production gates.</summary>
public enum AttestationKind
{
    SandboxMigrationCompleted,
    DifferentialBehaviorTestPassed,
    DataReconciliationPassed,
    HumanAcceptanceSigned,
    GeneratedApplicationTestsPassed,
}

/// <summary>A workspace-relative artifact path. Never a credential, URL, or connection string.</summary>
public sealed record ArtifactReference(
    [property: Description("Workspace-relative path. Rooted paths and '..' traversal are rejected.")]
    string Path,
    ArtifactKind Kind,
    string Description);

/// <summary>The destination stack a run converts the Oracle Forms application into.</summary>
public sealed record TargetStack
{
    public FrontEndStack FrontEnd { get; init; } = FrontEndStack.React;

    public BackEndStack BackEnd { get; init; } = BackEndStack.JavaSpringBoot;

    [Description("Database engine the Oracle schema, PL/SQL, and data are converted to.")]
    public required DatabaseTarget Database { get; init; }
}

/// <summary>A signed statement that a phase actually completed, supplied by the executing adapter.</summary>
public sealed record MigrationAttestation
{
    public required AttestationKind Kind { get; init; }

    [Description("True only when the attested activity completed successfully.")]
    public required bool Succeeded { get; init; }

    [Description("Identity of the human or pipeline that signed the attestation.")]
    public required string AttestedBy { get; init; }

    public string Summary { get; init; } = string.Empty;

    [Description("Workspace-relative artifacts that back the attestation. At least one is required for the attestation to unlock a gate; rooted, URI, and traversal paths are rejected.")]
    public IReadOnlyList<ArtifactReference> Artifacts { get; init; } = [];
}

/// <summary>Inbound request for an end-to-end migration run plan.</summary>
public sealed record MigrationRunRequest
{
    [Description("Engagement or work-item identifier used for auditing.")]
    public required string EngagementId { get; init; }

    [Description("Name of the Oracle Forms application being migrated.")]
    public required string ApplicationName { get; init; }

    [Description("How far this run is requested to go. The planner may authorize a lower mode.")]
    public ExecutionMode RequestedMode { get; init; } = ExecutionMode.PlanOnly;

    [Description("Destination stack: React front end, Java/Spring Boot back end, and the database target.")]
    public required TargetStack Target { get; init; }

    [Description("Oracle Forms release of the source estate, or 'unknown' when it has not been established. Accepted values span 6i through 12c; newer releases are recorded for assessment only. The value never opens a gate: binary Forms source still requires an operator-produced textual export before an application tier can be generated.")]
    public string OracleFormsVersion { get; init; } = "unknown";

    [Description("Oracle Database release behind the supplied export, or 'unknown' when it has not been established. Conversion reads supplied SQL text at any recognized release; it proves the constructs in that export and nothing about an instance this fleet never contacted.")]
    public string OracleDatabaseVersion { get; init; } = "unknown";

    [Description("Workspace-relative directory holding the authoritative Oracle Forms and database source.")]
    public required string SourceRoot { get; init; }

    [Description("Workspace-relative directory the run writes generated artifacts into.")]
    public required string OutputRoot { get; init; }

    [Description("Identifier of the disposition ledger this run generates under, or null. It is a locator and nothing else: the server resolves the ledger, its project, its tenant and its source snapshot itself, and a phase is answered from the stored rows rather than from anything named here.")]
    public string? DispositionLedgerId { get; init; }

    [Description("Supplied source artifacts. Generation requires Forms source or XML, PL/SQL, schema, and a regression baseline.")]
    public IReadOnlyList<EvidenceItem> Evidence { get; init; } = [];

    [Description("Approval of the assessment plan. Never sufficient on its own to mutate a sandbox or production.")]
    public HumanApproval PlanApproval { get; init; } = HumanApproval.Pending;

    [Description("Separate approval authorizing sandbox mutation. Requires an approver identity.")]
    public HumanApproval ExecutionApproval { get; init; } = HumanApproval.Pending;

    [Description("Separate approval authorizing production cutover. Requires an approver identity.")]
    public HumanApproval ProductionApproval { get; init; } = HumanApproval.Pending;

    [Description("Attestations returned by execution adapters. Production cutover requires successful sandbox, reconciliation, and acceptance attestations, each citing at least one valid workspace-relative artifact.")]
    public IReadOnlyList<MigrationAttestation> Attestations { get; init; } = [];
}

/// <summary>Plan for one lifecycle phase, including its ownership, mutation class, and gate state.</summary>
public sealed record PhasePlan(
    MigrationPhase Phase,
    FleetRole Owner,
    PhaseStatus Status,
    MutationClass Mutation,
    ExecutionMode RequiredMode,
    bool RequiresApproval,
    string Objective,
    IReadOnlyList<string> RequiredInputs,
    IReadOnlyList<ArtifactReference> ExpectedOutputs,
    IReadOnlyList<string> Tooling,
    IReadOnlyList<string> Blockers);

/// <summary>Request-independent ownership and gate class for one lifecycle phase.</summary>
public sealed record PhaseOwnership(
    MigrationPhase Phase,
    FleetRole Owner,
    MutationClass Mutation,
    ExecutionMode RequiredMode,
    bool RequiresApproval,
    string Objective);

/// <summary>Auditable output of one run-planning call. No process was run and no database was contacted.</summary>
public sealed record MigrationRunPlan(
    string EngagementId,
    string ApplicationName,
    ExecutionMode RequestedMode,
    ExecutionMode AuthorizedMode,
    TargetStack Target,
    IReadOnlyList<PhasePlan> Phases,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Disclaimers);
