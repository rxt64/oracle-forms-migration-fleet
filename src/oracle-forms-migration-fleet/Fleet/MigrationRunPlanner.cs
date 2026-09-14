// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Fleet;

/// <summary>
/// Deterministic planner for an end-to-end Oracle Forms migration run. It is pure: it never starts a
/// process, never writes a file, and never connects to Oracle, PostgreSQL, or SQL Server. It decides
/// which lifecycle phases are authorized, who owns them, what they consume, and what they must produce.
/// Execution adapters do the work and report back through <see cref="MigrationAttestation"/>.
/// </summary>
public static class MigrationRunPlanner
{
    public static IReadOnlyList<string> Disclaimers { get; } =
    [
        "This output is a migration run plan. This tool did not run any process, write any file, or connect to any database.",
        "A phase is only complete when an execution adapter returns artifacts and a matching attestation that cites those artifacts by workspace-relative path. Absent such an attestation, nothing was generated or migrated.",
        "Neither SSMA nor Ora2Pg converts Oracle Forms UI or runtime behavior; Forms conversion is owned by the fleet's own conversion adapter, which this repository designs and plans rather than implements.",
        "Sandbox mutation and production cutover each require their own recorded approval. Assessment plan approval authorizes neither.",
        "Phase tooling names the adapter a phase would call. Adapters marked as proposed are not implemented in this repository, so their phases can be planned but not executed here.",
    ];

    /// <summary>Evidence that must be present and verified before any artifact may be generated.</summary>
    private static readonly EvidenceKind[] s_plSqlAndSchema =
        [EvidenceKind.PlSqlProgramUnit, EvidenceKind.DatabaseSchemaExport];

    private static readonly EvidenceKind[] s_formsSource =
        [EvidenceKind.FormsModuleSource, EvidenceKind.FormsXmlExport];

    /// <summary>A required input satisfied by any one of the '|'-separated evidence kinds.</summary>
    private const string FormsSourceInput =
        $"{nameof(EvidenceKind.FormsModuleSource)}|{nameof(EvidenceKind.FormsXmlExport)}";

    /// <summary>Request-independent view of the lifecycle, used to publish ownership in the agent prompt.</summary>
    public static IReadOnlyList<PhaseOwnership> Lifecycle { get; } =
        [.. Blueprint(new MigrationRunRequest
        {
            EngagementId = "reference",
            ApplicationName = "reference",
            Target = new TargetStack { Database = DatabaseTarget.AzureSqlDatabase },
            SourceRoot = "source",
            OutputRoot = "output",
        }).Select(phase => new PhaseOwnership(
            phase.Phase, phase.Owner, phase.Mutation, phase.RequiredMode, phase.RequiresApproval, phase.Objective))];

    public static MigrationRunPlan Plan(MigrationRunRequest? request)
    {
        if (request is null)
        {
            return Blocked(
                new MigrationRunRequest
                {
                    EngagementId = string.Empty,
                    ApplicationName = string.Empty,
                    Target = new TargetStack { Database = DatabaseTarget.Undetermined },
                    SourceRoot = string.Empty,
                    OutputRoot = string.Empty,
                },
                ["Request is null."]);
        }

        List<string> blockers = [];
        List<string> assumptions = [];

        if (string.IsNullOrWhiteSpace(request.EngagementId))
        {
            blockers.Add("EngagementId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ApplicationName))
        {
            blockers.Add("ApplicationName is required.");
        }

        if (FleetGuardrails.ContainsPotentialSecret(request.EngagementId) ||
            FleetGuardrails.ContainsPotentialSecret(request.ApplicationName) ||
            FleetGuardrails.ContainsPotentialSecret(request.SourceRoot) ||
            FleetGuardrails.ContainsPotentialSecret(request.OutputRoot))
        {
            blockers.Add("Request fields appear to contain credential material and were rejected.");
        }

        if (ContainsPotentialSecret(request.PlanApproval) ||
            ContainsPotentialSecret(request.ExecutionApproval) ||
            ContainsPotentialSecret(request.ProductionApproval))
        {
            blockers.Add("Approval details appear to contain credential material and were rejected.");
        }

        if (request.Evidence is not null && request.Evidence.Any(ContainsPotentialSecret))
        {
            blockers.Add("Evidence metadata appears to contain credential material and was rejected.");
        }

        if (WorkspacePath.Validate(request.SourceRoot, nameof(request.SourceRoot)) is string sourceError)
        {
            blockers.Add(sourceError);
        }

        if (WorkspacePath.Validate(request.OutputRoot, nameof(request.OutputRoot)) is string outputError)
        {
            blockers.Add(outputError);
        }

        TargetStack? target = request.Target;
        if (target is null || !Enum.IsDefined(target.Database) || target.Database == DatabaseTarget.Undetermined)
        {
            blockers.Add("A concrete database target is required: PostgreSql, SqlServer, AzureSqlDatabase, or AzureSqlManagedInstance.");
        }

        if (!Enum.IsDefined(request.RequestedMode))
        {
            blockers.Add("RequestedMode has an unsupported value.");
        }

        if (blockers.Count > 0 || target is null)
        {
            return Blocked(request, blockers);
        }

        // Gate 1: generation needs real source, not just an inventory.
        List<string> generationBlockers = [];
        if (!HasAny(request.Evidence, s_formsSource))
        {
            generationBlockers.Add("Artifact generation requires verified FormsModuleSource or FormsXmlExport evidence.");
        }

        foreach (EvidenceKind kind in s_plSqlAndSchema.Where(kind => !HasAny(request.Evidence, [kind])))
        {
            generationBlockers.Add($"Artifact generation requires verified {kind} evidence.");
        }

        if (!HasAny(request.Evidence, [EvidenceKind.TestBaseline]))
        {
            generationBlockers.Add("Artifact generation requires a verified TestBaseline; without it no differential behavior claim can be made.");
        }

        // Gate 2: sandbox mutation needs its own approval, never the assessment plan approval.
        List<string> sandboxBlockers = [];
        if (!IsApproved(request.ExecutionApproval))
        {
            sandboxBlockers.Add("Sandbox migration requires an ExecutionApproval with Decision=Approved and an ApproverId. Plan approval does not authorize execution.");
        }
        else if (IsApproved(request.PlanApproval) &&
                 string.Equals(request.PlanApproval.ApproverId, request.ExecutionApproval.ApproverId, StringComparison.OrdinalIgnoreCase))
        {
            assumptions.Add("The plan approver and the execution approver are the same identity; segregation of duties was not demonstrated.");
        }

        // Gate 3: production needs its own approval plus independent attestations.
        List<string> productionBlockers = [];
        if (!IsApproved(request.ProductionApproval))
        {
            productionBlockers.Add("Production cutover requires a ProductionApproval with Decision=Approved and an ApproverId, separate from execution approval.");
        }
        else if (IsApproved(request.ExecutionApproval) &&
                 string.Equals(request.ExecutionApproval.ApproverId, request.ProductionApproval.ApproverId, StringComparison.OrdinalIgnoreCase))
        {
            productionBlockers.Add("Production cutover requires execution and production approvals from distinct identities.");
        }

        AttestationKind[] requiredAttestations =
        [
            AttestationKind.SandboxMigrationCompleted,
            AttestationKind.DataReconciliationPassed,
            AttestationKind.HumanAcceptanceSigned,
        ];

        foreach (AttestationKind kind in requiredAttestations.Where(kind => !HasAttestation(request.Attestations, kind)))
        {
            productionBlockers.Add(
                $"Production cutover requires a successful {kind} attestation with an attesting identity and at least one " +
                "workspace-relative backing artifact; rooted, URI, traversal, and credential-like fields are rejected.");
        }

        // The ladder is climbed per phase, not on the union of every phase's evidence. Gating the sandbox
        // rung on Forms binaries would block loading rows into PostgreSQL for want of a file no data phase
        // opens, and the only way past it would be to tick a box the operator cannot stand behind.
        bool AnyPhaseClearsEvidence(MutationClass mutation) =>
            Blueprint(request).Any(candidate =>
                candidate.Mutation == mutation && RelevantTo(candidate.Phase, generationBlockers).Count == 0);

        ExecutionMode authorized = ExecutionMode.PlanOnly;
        if (generationBlockers.Count == 0 || AnyPhaseClearsEvidence(MutationClass.WorkspaceArtifactWrite))
        {
            authorized = ExecutionMode.GenerateArtifacts;

            if (sandboxBlockers.Count == 0 && AnyPhaseClearsEvidence(MutationClass.SandboxDatabaseWrite))
            {
                authorized = ExecutionMode.SandboxMigration;

                // Production keeps the union: a cutover rests on claims about the whole application,
                // including the Forms behaviour nothing here has read.
                if (productionBlockers.Count == 0 && generationBlockers.Count == 0)
                {
                    authorized = ExecutionMode.ProductionCutover;
                }
            }
        }

        if (authorized > request.RequestedMode)
        {
            authorized = request.RequestedMode;
        }

        List<PhasePlan> phases =
            [.. Blueprint(request).Select(phase => Resolve(phase, request, authorized, generationBlockers, sandboxBlockers, productionBlockers))];

        // A phase that cleared its own evidence may run even when an unrelated requirement is outstanding,
        // so the header must report what was actually authorized rather than the strictest gate.
        if (authorized == ExecutionMode.PlanOnly &&
            phases.Any(phase => phase.Status == PhaseStatus.Planned && phase.Mutation != MutationClass.None))
        {
            authorized = ExecutionMode.GenerateArtifacts;
        }

        List<string> planBlockers = [.. phases
            .Where(phase => phase.Status is not (PhaseStatus.Planned or PhaseStatus.NotRequested))
            .SelectMany(phase => phase.Blockers.Select(blocker => $"{phase.Phase}: {blocker}"))
            .Distinct(StringComparer.Ordinal)];

        return new MigrationRunPlan(
            request.EngagementId,
            request.ApplicationName,
            request.RequestedMode,
            authorized,
            target,
            phases,
            assumptions,
            planBlockers,
            Disclaimers);
    }

    private static PhasePlan ResolveInputs(PhasePlan phase, MigrationRunRequest request)
    {
        IReadOnlyList<string> missing = MissingInputs(phase, request);
        return missing.Count == 0
            ? phase with { Status = PhaseStatus.Planned }
            : phase with { Status = PhaseStatus.BlockedOnEvidence, Blockers = missing };
    }

    /// <summary>
    /// Narrows the generation gate to the evidence a phase actually consumes.
    ///
    /// Gating every phase on the union trained operators to tick boxes they could not stand behind just to
    /// proceed, which is the failure this product exists to prevent: a schema conversion was refused for
    /// want of Forms binaries it never reads. A phase is still blocked by anything it does rely on.
    /// </summary>
    private static IReadOnlyList<string> RelevantTo(MigrationPhase phase, IReadOnlyList<string> generationBlockers)
    {
        EvidenceKind[] consumed = phase switch
        {
            // Emits DDL from the schema export, and lists PL/SQL it will not translate. Reads no Forms module.
            MigrationPhase.DatabaseConversion => [EvidenceKind.DatabaseSchemaExport, EvidenceKind.PlSqlProgramUnit],

            // Generates the application tier from the converted schema. It reports Forms modules it cannot
            // read rather than reading them, so Forms source is not what gates it.
            MigrationPhase.ApplicationCodeConversion =>
                [EvidenceKind.DatabaseSchemaExport, EvidenceKind.PlSqlProgramUnit, EvidenceKind.TestBaseline],

            // Indexes whatever source is present; it makes no behavioural claim, so no baseline is required.
            MigrationPhase.SourceAnalysis or MigrationPhase.SourceAcquisition =>
                [EvidenceKind.FormsModuleSource, EvidenceKind.FormsXmlExport, EvidenceKind.PlSqlProgramUnit, EvidenceKind.DatabaseSchemaExport],

            // Loads rows from the supplied data export into the converted schema. It opens no Forms module,
            // and its own approval gate is what authorizes the write.
            MigrationPhase.SandboxDataMigration => [EvidenceKind.DatabaseSchemaExport],

            // Compares what landed against the source, so it needs the baseline it compares against.
            MigrationPhase.DataReconciliation or MigrationPhase.DifferentialBehaviorTesting =>
                [EvidenceKind.DatabaseSchemaExport, EvidenceKind.TestBaseline],

            _ => [],
        };

        return consumed.Length == 0
            ? generationBlockers
            : [.. generationBlockers.Where(blocker => consumed.Any(kind => blocker.Contains(kind.ToString(), StringComparison.Ordinal)))];
    }

    private static PhasePlan Resolve(
        PhasePlan phase,
        MigrationRunRequest request,
        ExecutionMode authorized,
        IReadOnlyList<string> generationBlockers,
        IReadOnlyList<string> sandboxBlockers,
        IReadOnlyList<string> productionBlockers)
    {
        // Clearing the mode ladder is not the same as clearing your own evidence. Another phase may have
        // raised the authorized mode, so a mutating phase is still held to what it individually relies on.
        if (phase.RequiredMode <= authorized &&
            (phase.Mutation == MutationClass.None || RelevantTo(phase.Phase, generationBlockers).Count == 0))
        {
            return ResolveInputs(phase, request);
        }

        if (phase.RequiredMode > request.RequestedMode)
        {
            return phase with { Status = PhaseStatus.NotRequested };
        }

        // The caller asked for this phase but a gate denied it. Report the gate that applies.
        return phase.RequiredMode switch
        {
            ExecutionMode.GenerateArtifacts => RelevantTo(phase.Phase, generationBlockers) is { Count: 0 } && request.RequestedMode >= ExecutionMode.GenerateArtifacts
                ? ResolveInputs(phase, request)
                : phase with
                {
                    Status = PhaseStatus.BlockedOnEvidence,
                    Blockers = RelevantTo(phase.Phase, generationBlockers),
                },
            ExecutionMode.SandboxMigration when RelevantTo(phase.Phase, generationBlockers) is { Count: > 0 } sandboxEvidence => phase with
            {
                Status = PhaseStatus.BlockedOnEvidence,
                Blockers = sandboxEvidence,
            },
            ExecutionMode.SandboxMigration => phase with
            {
                Status = PhaseStatus.BlockedOnApproval,
                Blockers = sandboxBlockers,
            },
            _ when RelevantTo(phase.Phase, generationBlockers) is { Count: > 0 } productionEvidence => phase with
            {
                Status = PhaseStatus.BlockedOnEvidence,
                Blockers = productionEvidence,
            },
            _ when sandboxBlockers.Count > 0 => phase with
            {
                Status = PhaseStatus.BlockedOnApproval,
                Blockers = sandboxBlockers,
            },
            _ => phase with
            {
                Status = productionBlockers.Any(b => b.Contains("attestation", StringComparison.OrdinalIgnoreCase))
                    ? PhaseStatus.BlockedOnAttestation
                    : PhaseStatus.BlockedOnApproval,
                Blockers = productionBlockers,
            },
        };
    }

    private static IReadOnlyList<string> MissingInputs(PhasePlan phase, MigrationRunRequest request)
    {
        List<string> missing = [];
        foreach (string input in phase.RequiredInputs)
        {
            EvidenceKind[] alternatives = [.. input
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(part => Enum.TryParse(part, out EvidenceKind _))
                .Select(Enum.Parse<EvidenceKind>)];

            if (alternatives.Length > 0 && !HasAny(request.Evidence, alternatives))
            {
                missing.Add($"Verified {string.Join(" or ", alternatives)} evidence was not supplied.");
            }
        }

        return missing;
    }

    /// <summary>The full lifecycle, independent of gate state. Paths are workspace-relative only.</summary>
    private static IReadOnlyList<PhasePlan> Blueprint(MigrationRunRequest request)
    {
        string src = WorkspacePath.Normalize(request.SourceRoot);
        string root = WorkspacePath.Normalize(request.OutputRoot);
        DatabaseTarget database = request.Target.Database;
        string dbFolder = DatabaseFolder(database);
        IReadOnlyList<string> dbTooling = DatabaseTooling(database);

        return
        [
            new(MigrationPhase.SourceAcquisition, FleetRole.InventoryAnalyst, PhaseStatus.Planned,
                MutationClass.None, ExecutionMode.PlanOnly, RequiresApproval: false,
                "Collect the authoritative Forms, menu, library, report, PL/SQL, and schema source into the workspace and confirm provenance.",
                [nameof(EvidenceKind.FormsModuleInventory)],
                [new ArtifactReference($"{src}/", ArtifactKind.SourceInput, "Authoritative source tree under version control.")],
                ["Oracle Forms source control export", "Forms2XML / JDAPI for module structure extraction"],
                []),

            new(MigrationPhase.SourceAnalysis, FleetRole.DependencyMapper, PhaseStatus.Planned,
                // It writes the five analysis artifacts below, so "plan only" must not authorize it.
                MutationClass.WorkspaceArtifactWrite, ExecutionMode.GenerateArtifacts, RequiresApproval: false,
                "Parse modules, triggers, program units, libraries, and schema objects into a dependency graph and risk register.",
                [nameof(EvidenceKind.PlSqlProgramUnit), nameof(EvidenceKind.DatabaseSchemaExport)],
                [
                    new ArtifactReference($"{root}/analysis/dependency-graph.json", ArtifactKind.ValidationReport, "Module, library, report, and schema dependency graph."),
                    new ArtifactReference($"{root}/analysis/APPLICATION_INVENTORY.md", ArtifactKind.Documentation, "Forms, menus, libraries, reports, and program units with size and complexity signals."),
                    new ArtifactReference($"{root}/analysis/DATA_DICTIONARY.md", ArtifactKind.Documentation, "Tables, views, columns, types, and constraints referenced by the estate."),
                    new ArtifactReference($"{root}/analysis/DEPENDENCY_MAP.md", ArtifactKind.Documentation, "Human-readable module, library, and schema dependency narrative behind the graph."),
                    new ArtifactReference($"{root}/analysis/TECHNICAL_DEBT_REPORT.md", ArtifactKind.Documentation, "Obsolete constructs, duplication, dead code, and remediation risk ranking."),
                ],
                ["Forms2XML / JDAPI", "Fleet dependency analyzer"],
                []),

            new(MigrationPhase.DocumentationGeneration, FleetRole.DocumentationAuthor, PhaseStatus.Planned,
                MutationClass.WorkspaceArtifactWrite, ExecutionMode.GenerateArtifacts, RequiresApproval: false,
                "Generate human-readable documentation of the existing behavior: screens, navigation, validation rules, batch paths, and data model.",
                [FormsSourceInput, nameof(EvidenceKind.BusinessProcessCatalog)],
                [
                    new ArtifactReference($"{root}/docs/as-is-functional-spec.md", ArtifactKind.Documentation, "Per-module screens, blocks, triggers, and validation rules."),
                    new ArtifactReference($"{root}/docs/as-is-data-model.md", ArtifactKind.Documentation, "Tables, views, constraints, and PL/SQL entry points."),
                ],
                ["Fleet documentation generator"],
                []),

            new(MigrationPhase.SourceNormalization, FleetRole.ApplicationCodeConverter, PhaseStatus.Planned,
                MutationClass.WorkspaceArtifactWrite, ExecutionMode.GenerateArtifacts, RequiresApproval: false,
                "Normalize Forms modules, menus, PLLs, and OLBs into a stable intermediate representation before conversion.",
                [FormsSourceInput],
                [new ArtifactReference($"{root}/intermediate/forms-ir.json", ArtifactKind.NormalizedSource, "Normalized intermediate representation of the Forms estate.")],
                [
                    "Forms2XML / JDAPI",
                    "Fleet normalizer",
                    "Oracle Forms MCP (aoreshkov/oracle-forms-mcp) is a candidate parser/indexer for this phase only.",
                    "Oracle Forms MCP requires either the licensed Oracle frmf2xml/frmcmp_batch utilities via ORACLE_HOME or adjacent pre-converted XML/PLD input; the proprietary Oracle tools are not redistributed by the fleet.",
                    "Oracle Forms MCP is not target-code generation: it produces parsed structure and indexes, never React or Java output.",
                ],
                []),

            new(MigrationPhase.ApplicationCodeConversion, FleetRole.ApplicationCodeConverter, PhaseStatus.Planned,
                MutationClass.WorkspaceArtifactWrite, ExecutionMode.GenerateArtifacts, RequiresApproval: false,
                "Generate a pilot React and Java/Spring Boot application tier over the converted schema, bound to the Azure database with no Oracle in its data path, and report the Forms and PL/SQL behaviour that must be rebuilt by hand before the pilot is widened.",
                [nameof(EvidenceKind.DatabaseSchemaExport), nameof(EvidenceKind.PlSqlProgramUnit)],
                [
                    new ArtifactReference($"{root}/web/react/src", ArtifactKind.FrontEndCode, "React components, routing, and validation derived from Forms blocks and triggers."),
                    new ArtifactReference($"{root}/service/java-springboot/src/main/java", ArtifactKind.BackEndCode, "Java/Spring Boot services, controllers, and persistence for converted client-side logic."),
                    new ArtifactReference($"{root}/reports/pilot-component-mapping.md", ArtifactKind.Documentation, "Auditable pilot migration notes: source module to generated component and service mapping, manual edits, and open questions."),
                ],
                [
                    "Fleet Forms-to-React/Java converter (proposed local execution adapter, not implemented in this repository), applied pilot-first to a reviewed module slice before estate-wide conversion",
                    "Compiler-driven and AI-assisted repair loop over the generated React and Java sources (proposed local execution adapter)",
                    "Neither SSMA nor Ora2Pg is used here: they convert database code only, not Forms UI or runtime behavior.",
                ],
                []),

            new(MigrationPhase.DatabaseConversion, FleetRole.DatabaseConverter, PhaseStatus.Planned,
                MutationClass.WorkspaceArtifactWrite, ExecutionMode.GenerateArtifacts, RequiresApproval: false,
                $"Convert Oracle schema, types, and PL/SQL to {database} and record every unconvertible construct for manual remediation.",
                [nameof(EvidenceKind.DatabaseSchemaExport), nameof(EvidenceKind.PlSqlProgramUnit)],
                [
                    new ArtifactReference($"{root}/database/{dbFolder}/schema", ArtifactKind.DatabaseSchema, $"Converted {database} schema and programmable objects."),
                    new ArtifactReference($"{root}/database/{dbFolder}/conversion-report.md", ArtifactKind.ValidationReport, "Type mappings, unsupported constructs, and manual remediation list."),
                ],
                dbTooling,
                []),

            new(MigrationPhase.BuildAndStaticValidation, FleetRole.BuildAndTestEngineer, PhaseStatus.Planned,
                MutationClass.WorkspaceArtifactWrite, ExecutionMode.GenerateArtifacts, RequiresApproval: false,
                "Build the React and Java outputs, run static analysis, and fail the run on any unresolved compilation or lint error.",
                [],
                [new ArtifactReference($"{root}/reports/build-and-static-analysis.json", ArtifactKind.ValidationReport, "Build, lint, and static analysis results for the generated code.")],
                ["npm/vite build", "Maven or Gradle build", "Static analysis over generated sources"],
                []),

            new(MigrationPhase.DifferentialBehaviorTesting, FleetRole.BuildAndTestEngineer, PhaseStatus.Planned,
                MutationClass.SandboxDatabaseWrite, ExecutionMode.SandboxMigration, RequiresApproval: true,
                "Replay the regression baseline against the legacy behavior and the converted stack and report every behavioral difference.",
                [nameof(EvidenceKind.TestBaseline)],
                [
                    new ArtifactReference($"{root}/tests/differential", ArtifactKind.TestSuite, "Generated differential test suite derived from the regression baseline."),
                    new ArtifactReference($"{root}/reports/differential-behavior.json", ArtifactKind.ValidationReport, "Per-scenario behavioral differences between legacy and converted stacks."),
                ],
                ["Sandbox execution adapter", "Baseline replay harness"],
                []),

            new(MigrationPhase.SandboxDataMigration, FleetRole.DataMigrationEngineer, PhaseStatus.Planned,
                MutationClass.SandboxDatabaseWrite, ExecutionMode.SandboxMigration, RequiresApproval: true,
                $"Apply the converted schema to the {database} sandbox, load the INSERT statements found in the supplied export, and read row counts back from the target.",
                // The adapter loads what the export contains; it does not sample, so it never reads a
                // DataProfile. Requiring one would only teach an operator to tick a box for a document
                // nothing opens.
                [nameof(EvidenceKind.DatabaseSchemaExport)],
                [
                    new ArtifactReference($"{root}/database/{dbFolder}/data-migration", ArtifactKind.DataMigrationScript, "Data movement definitions executed by the adapter against the sandbox only."),
                    new ArtifactReference($"{root}/reports/sandbox-data-migration.json", ArtifactKind.ValidationReport, "Sandbox load results, rejected rows, and duration."),
                ],
                dbTooling,
                []),

            new(MigrationPhase.DataReconciliation, FleetRole.ReconciliationAnalyst, PhaseStatus.Planned,
                MutationClass.WorkspaceArtifactWrite, ExecutionMode.SandboxMigration, RequiresApproval: true,
                "Count the rows the supplied export should have produced, read the target back, and report every table that does not match.",
                // It counts INSERTs in the export and reads the target; it opens no separate profile.
                [nameof(EvidenceKind.DatabaseSchemaExport)],
                [new ArtifactReference($"{root}/reports/data-reconciliation.json", ArtifactKind.ReconciliationReport, "Row counts, checksums, and tolerance breaches by table.")],
                ["Reconciliation adapter (read-only against source, read-only against sandbox)"],
                []),

            new(MigrationPhase.HumanAcceptance, FleetRole.AcceptanceCoordinator, PhaseStatus.Planned,
                MutationClass.None, ExecutionMode.SandboxMigration, RequiresApproval: true,
                "Collect business, security, data, user, and operations acceptance against the sandbox. Generated code and pass rates are not acceptance.",
                [nameof(EvidenceKind.TestBaseline), nameof(EvidenceKind.BusinessProcessCatalog)],
                [new ArtifactReference($"{root}/reports/acceptance-record.md", ArtifactKind.ValidationReport, "Named sign-offs per acceptance domain with the evidence each reviewed.")],
                ["Human review; no automation substitutes for this phase"],
                []),

            new(MigrationPhase.ProductionCutover, FleetRole.Orchestrator, PhaseStatus.Planned,
                MutationClass.ProductionWrite, ExecutionMode.ProductionCutover, RequiresApproval: true,
                "Execute the approved cutover runbook against production and keep the rehearsed rollback available throughout.",
                [nameof(EvidenceKind.CutoverAndRollbackPlan)],
                [new ArtifactReference($"{root}/reports/cutover-runbook.md", ArtifactKind.CutoverRunbook, "Sequenced cutover, verification, and rollback steps with owners.")],
                ["Production execution adapter under a separate production approval"],
                []),
        ];
    }

    private static string DatabaseFolder(DatabaseTarget target) => target switch
    {
        DatabaseTarget.PostgreSql => "postgresql",
        DatabaseTarget.SqlServer => "sqlserver",
        DatabaseTarget.AzureSqlDatabase => "azure-sql-database",
        DatabaseTarget.AzureSqlManagedInstance => "azure-sql-managed-instance",
        _ => "undetermined",
    };

    /// <summary>Target-specific database tooling. Neither option converts Oracle Forms UI or runtime behavior.</summary>
    private static IReadOnlyList<string> DatabaseTooling(DatabaseTarget target) => target switch
    {
        DatabaseTarget.PostgreSql =>
        [
            "Ora2Pg — Oracle schema, PL/SQL, and data conversion to PostgreSQL.",
            "Ora2Pg does not convert Oracle Forms UI or runtime behavior; unconverted PL/SQL requires manual remediation.",
        ],
        DatabaseTarget.SqlServer or DatabaseTarget.AzureSqlDatabase or DatabaseTarget.AzureSqlManagedInstance =>
        [
            "SSMA (SQL Server Migration Assistant) for Oracle — schema, PL/SQL, and data conversion to the SQL Server family.",
            "SSMA does not convert Oracle Forms UI or runtime behavior; conversion reports require manual remediation and SYS/SYSTEM schemas are excluded.",
        ],
        _ => ["No database tooling can be selected until a concrete database target is supplied."],
    };

    private static bool HasAny(IReadOnlyList<EvidenceItem>? evidence, IReadOnlyList<EvidenceKind> kinds) =>
        evidence?.Any(item => item is { IsVerified: true } && kinds.Contains(item.Kind)) == true;

    private static bool IsApproved(HumanApproval? approval) =>
        approval is { Decision: ApprovalDecision.Approved } && !string.IsNullOrWhiteSpace(approval.ApproverId);

    private static bool ContainsPotentialSecret(HumanApproval? approval) =>
        approval is not null &&
        (FleetGuardrails.ContainsPotentialSecret(approval.ApproverId) ||
         FleetGuardrails.ContainsPotentialSecret(approval.Notes));

    private static bool ContainsPotentialSecret(EvidenceItem? evidence) =>
        evidence is not null &&
        (FleetGuardrails.ContainsPotentialSecret(evidence.Id) ||
         FleetGuardrails.ContainsPotentialSecret(evidence.Source) ||
         FleetGuardrails.ContainsPotentialSecret(evidence.Summary));

    private static bool HasAttestation(IReadOnlyList<MigrationAttestation>? attestations, AttestationKind kind) =>
        attestations?.Any(a => a is { Succeeded: true } && a.Kind == kind && IsBacked(a)) == true;

    /// <summary>
    /// An attestation only unlocks a gate when it names a signer and cites at least one backing artifact whose
    /// workspace-relative path is valid. The planner is pure, so the artifact is not read from disk.
    /// </summary>
    private static bool IsBacked(MigrationAttestation attestation)
    {
        if (string.IsNullOrWhiteSpace(attestation.AttestedBy) ||
            FleetGuardrails.ContainsPotentialSecret(attestation.AttestedBy) ||
            FleetGuardrails.ContainsPotentialSecret(attestation.Summary))
        {
            return false;
        }

        IReadOnlyList<ArtifactReference> artifacts = attestation.Artifacts ?? [];
        return artifacts.Count > 0 && artifacts.All(artifact =>
            artifact is not null &&
            Enum.IsDefined(artifact.Kind) &&
            WorkspacePath.Validate(artifact.Path, "attestation artifact") is null &&
            !FleetGuardrails.ContainsPotentialSecret(artifact.Path) &&
            !FleetGuardrails.ContainsPotentialSecret(artifact.Description));
    }

    private static MigrationRunPlan Blocked(MigrationRunRequest request, IReadOnlyList<string> blockers) =>
        new(request.EngagementId ?? string.Empty,
            request.ApplicationName ?? string.Empty,
            Enum.IsDefined(request.RequestedMode) ? request.RequestedMode : ExecutionMode.PlanOnly,
            ExecutionMode.PlanOnly,
            request.Target is { } target && Enum.IsDefined(target.Database)
                ? target
                : new TargetStack { Database = DatabaseTarget.Undetermined },
            [],
            [],
            blockers,
            Disclaimers);
}

/// <summary>Workspace-relative path rules. Rooted paths, UNC paths, URI/stream syntax, and '..' traversal are rejected.</summary>
public static class WorkspacePath
{
    public static string? Validate(string? path, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return $"{fieldName} is required and must be a workspace-relative path.";
        }

        string normalized = Normalize(path);

        if (Path.IsPathRooted(path) || Path.IsPathRooted(normalized) ||
            normalized.StartsWith('/') || path.StartsWith('\\') || path.Contains("://", StringComparison.Ordinal))
        {
            return $"{fieldName} must be workspace-relative; rooted, UNC, and URI paths are rejected.";
        }

        // A colon anywhere in a segment covers drive letters, scheme-like prefixes, and NTFS alternate data streams.
        if (normalized.Split('/').Any(segment => segment == ".." || segment.Contains(':', StringComparison.Ordinal)))
        {
            return $"{fieldName} must not traverse outside the workspace, reference a drive, or contain ':'.";
        }

        return null;
    }

    /// <summary>Converts to forward slashes and trims trailing separators. Does not touch the file system.</summary>
    public static string Normalize(string path) =>
        (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');
}
