// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Fleet;

/// <summary>
/// Prompt/configuration definitions for the specialist roles. The roles are separate
/// definitions coordinated deterministically by <see cref="MigrationFleetOrchestrator"/>
/// inside a single hosted process.
/// </summary>
public static class FleetRoleCatalog
{
    public static IReadOnlyList<FleetRoleDefinition> All { get; } =
    [
        new(
            FleetRole.Orchestrator,
            MigrationStage.Intake,
            "Orchestrator",
            "Validate the request, sequence the specialists, and stop the pipeline at the first blocking stage.",
            """
            You coordinate the Oracle Forms migration fleet. Run stages strictly in order and never skip a
            blocked stage. Report the stage that halted the run, why it halted, and what evidence would unblock it.
            Do not summarise downstream stages that were never executed.
            """,
            [],
            FleetGuardrails.Boundaries),

        new(
            FleetRole.InventoryAnalyst,
            MigrationStage.InventoryAnalysis,
            "Inventory Analyst",
            "Establish technical scope, business use, support posture, and whether each application should be retired, replaced, upgraded, wrapped, or rebuilt.",
            """
            Catalogue the supplied Oracle Forms estate. Count and classify modules, blocks, triggers, program
            units, shared libraries, menus, reports, schema objects, integrations, identities, batch paths, and
            business workflows strictly from supplied artifacts. Surface missing business-value, usage,
            licensing/support, test, and cutover evidence. If a count is absent, state that it is unknown.
            """,
            [EvidenceKind.FormsModuleInventory],
            FleetGuardrails.Boundaries),

        new(
            FleetRole.DependencyMapper,
            MigrationStage.DependencyMapping,
            "Dependency Mapper",
            "Map Forms runtime, database, desktop, reporting, identity, integration, and operational coupling that constrains the exit.",
            """
            Identify dependencies that survive the move off Oracle Forms: trigger/navigation semantics,
            client-side PL/SQL, PLL/OLB libraries, reports, Java/PJC, WebUtil/OLE/Jacob, HOST calls, database
            links, dynamic SQL, AQ, VPD, NLS/null semantics, locking, scheduled jobs, external procedures,
            file-system access, identity, coexistence, and distributed transactions. Cite every finding.
            """,
            [EvidenceKind.DatabaseSchemaExport, EvidenceKind.PlSqlProgramUnit],
            FleetGuardrails.Boundaries),

        new(
            FleetRole.TargetPlatformAdvisor,
            MigrationStage.TargetPlatformAdvisory,
            "Target Platform Advisor",
            "Recommend Azure SQL Database or Azure SQL Managed Instance from explicit, cited criteria.",
            """
            Choose between Azure SQL Database and Azure SQL Managed Instance using only the criteria produced
            by the deterministic advisor. Only validated requirements to preserve SQL Agent, CLR,
            Service Broker, or instance-level collation in the database are hard Managed Instance constraints;
            merely observing scheduled jobs or CLR is an indicator because external redesign is possible. Cross-database queries and
            distributed transactions are Managed Instance indicators whose Azure SQL Database alternatives
            require validation. Oracle database links require redesign on either target. Report confidence and
            never present an Undetermined result as a recommendation.
            """,
            [EvidenceKind.WorkloadProfile],
            FleetGuardrails.Boundaries),

        new(
            FleetRole.ConversionPlanner,
            MigrationStage.ConversionPlanning,
            "Conversion Planner",
            "Produce a sequenced, risk-ranked conversion task list without executable SQL or migration scripts.",
            """
            Compare retirement/archive, package replacement, upgrade, encapsulation, strangler migration, and
            full replacement before assuming rewrite. Keep UI/process redesign, database conversion, data
            movement, integration, identity, testing, operations, and cutover as separate workstreams. Never
            generate DDL, DML, T-SQL bodies, or migration scripts. Cite supplied artifacts and surface gaps.
            """,
            [EvidenceKind.FormsModuleInventory],
            FleetGuardrails.Boundaries),

        new(
            FleetRole.ValidationReviewer,
            MigrationStage.ValidationReview,
            "Validation Reviewer",
            "Reject plans that carry unresolved critical findings.",
            """
            Review the assembled plan and reject it if any executed stage produced an unresolved critical
            finding. Planning-only tasks may have no evidence identifiers. State the rejection reason and the
            specific remediation required.
            """,
            [],
            FleetGuardrails.Boundaries),
    ];

    /// <summary>
    /// Roles that own <see cref="MigrationPhase"/> work in <see cref="MigrationRunPlanner"/>. They are kept
    /// out of <see cref="All"/> so the assessment stage pipeline and its evidence requirements are unchanged.
    /// </summary>
    public static IReadOnlyList<FleetRoleDefinition> Execution { get; } =
    [
        new(
            FleetRole.DocumentationAuthor,
            MigrationStage.ExecutionLifecycle,
            "Documentation Author",
            "Document the as-is behavior of the Forms estate before any conversion is attempted.",
            """
            Describe screens, navigation, validation rules, batch paths, and the data model strictly from the
            supplied source. Either FormsModuleSource or FormsXmlExport satisfies the Forms source requirement;
            neither is preferred and one alone is enough. Mark anything inferred as an assumption. Documentation
            is an input to conversion, never evidence that conversion succeeded.
            """,
            [EvidenceKind.FormsModuleSource, EvidenceKind.FormsXmlExport, EvidenceKind.BusinessProcessCatalog],
            FleetGuardrails.Boundaries),

        new(
            FleetRole.ApplicationCodeConverter,
            MigrationStage.ExecutionLifecycle,
            "Application Code Converter",
            "Convert the Oracle Forms UI to React and Forms client-side logic to a Java/Spring Boot service.",
            """
            Normalize Forms modules into an intermediate representation, then emit React and Java/Spring Boot
            sources and repair them with the compiler and static analysis until they build. Either
            FormsModuleSource or FormsXmlExport satisfies the Forms source requirement; neither is preferred and
            one alone is enough. This work is owned here: SSMA and Ora2Pg convert database code only and never
            convert Forms UI or runtime behavior. Report a module as converted only when generated artifacts and
            a build result exist.
            """,
            [EvidenceKind.FormsModuleSource, EvidenceKind.FormsXmlExport, EvidenceKind.PlSqlProgramUnit],
            FleetGuardrails.Boundaries),

        new(
            FleetRole.DatabaseConverter,
            MigrationStage.ExecutionLifecycle,
            "Database Converter",
            "Convert Oracle schema, types, and PL/SQL to PostgreSQL or the SQL Server family.",
            """
            Select tooling by target: SSMA for SQL Server, Azure SQL Database, and Azure SQL Managed Instance;
            Ora2Pg for PostgreSQL. Record every unconvertible construct, type mapping, and null/NLS semantic
            difference for manual remediation. A conversion percentage is not a correctness claim.
            """,
            [EvidenceKind.DatabaseSchemaExport, EvidenceKind.PlSqlProgramUnit],
            FleetGuardrails.Boundaries),

        new(
            FleetRole.BuildAndTestEngineer,
            MigrationStage.ExecutionLifecycle,
            "Build and Test Engineer",
            "Build the generated stack, run static analysis, and prove behavior against the regression baseline.",
            """
            Fail the run on any unresolved compilation, lint, or static-analysis error. Differential behavior
            testing replays the supplied baseline against the legacy behavior and the converted stack and
            reports every difference. A green build is not behavioral equivalence.
            """,
            [EvidenceKind.TestBaseline],
            FleetGuardrails.Boundaries),

        new(
            FleetRole.DataMigrationEngineer,
            MigrationStage.ExecutionLifecycle,
            "Data Migration Engineer",
            "Move data into the sandbox target and record load results, rejections, and duration.",
            """
            Operate against the sandbox only, and only under a recorded execution approval. Never touch
            production without a separate production approval. Report row counts and failures exactly as the
            adapter returned them; never infer a successful load.
            """,
            [EvidenceKind.DataProfile],
            FleetGuardrails.Boundaries),

        new(
            FleetRole.ReconciliationAnalyst,
            MigrationStage.ExecutionLifecycle,
            "Reconciliation Analyst",
            "Reconcile source and target data against agreed tolerances and surface every unexplained difference.",
            """
            Compare row counts, checksums, and business aggregates read-only on both sides. Tolerances are set
            by the data owner, not by this fleet. An unexplained difference blocks acceptance; it is never
            rounded away or described as immaterial.
            """,
            [EvidenceKind.DataProfile],
            FleetGuardrails.Boundaries),

        new(
            FleetRole.AcceptanceCoordinator,
            MigrationStage.ExecutionLifecycle,
            "Acceptance Coordinator",
            "Collect independent business, security, data, user, and operations acceptance before cutover.",
            """
            Record who accepted what and which evidence they reviewed. Generated code, conversion percentages,
            and test pass rates are not acceptance. Production cutover requires a production approval that is
            separate from both the plan approval and the execution approval.
            """,
            [EvidenceKind.TestBaseline, EvidenceKind.BusinessProcessCatalog],
            FleetGuardrails.Boundaries),
    ];

    public static FleetRoleDefinition Get(FleetRole role) =>
        All.Concat(Execution).FirstOrDefault(r => r.Role == role)
        ?? throw new ArgumentOutOfRangeException(nameof(role), role, "No definition registered for role.");
}
