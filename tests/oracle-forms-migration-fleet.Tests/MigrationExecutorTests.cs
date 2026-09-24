// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Fleet.Execution.Adapters;

namespace OracleFormsMigrationFleet.Tests;

public class MigrationExecutorTests
{
    private const string Operator = "migration-operator@contoso.com";

    private static IReadOnlyList<EvidenceItem> FullEvidence() =>
    [
        Requests.Evidence("EV-INV", EvidenceKind.FormsModuleInventory),
        Requests.Evidence("EV-SRC", EvidenceKind.FormsModuleSource),
        Requests.Evidence("EV-PLSQL", EvidenceKind.PlSqlProgramUnit),
        Requests.Evidence("EV-SCHEMA", EvidenceKind.DatabaseSchemaExport),
        Requests.Evidence("EV-TEST", EvidenceKind.TestBaseline),
        Requests.Evidence("EV-DATA", EvidenceKind.DataProfile),
        Requests.Evidence("EV-PROCESS", EvidenceKind.BusinessProcessCatalog),
    ];

    private static MigrationRunRequest Request(
        DatabaseTarget database = DatabaseTarget.PostgreSql,
        ExecutionMode mode = ExecutionMode.GenerateArtifacts,
        IReadOnlyList<EvidenceItem>? evidence = null,
        HumanApproval? executionApproval = null) => new()
        {
            EngagementId = "ENG-EXEC",
            ApplicationName = "ORDERS",
            RequestedMode = mode,
            Target = new TargetStack { Database = database },
            SourceRoot = "legacy/forms",
            OutputRoot = "out/orders",
            Evidence = evidence ?? FullEvidence(),
            PlanApproval = Requests.Approved("plan-owner@contoso.com"),
            ExecutionApproval = executionApproval ?? HumanApproval.Pending,
        };

    private static TemporaryWorkspace SeededWorkspace()
    {
        TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/001_schema.sql", OracleSamples.Schema);
        workspace.WriteFile("legacy/forms/db/003_plsql.sql", OracleSamples.PlSql);
        workspace.WriteBytes("legacy/forms/ui/ACCOUNT_OPEN.fmb", [0x0A, 0x46, 0x4F, 0x52, 0x4D, 0x00, 0xFF, 0xFE]);
        return workspace;
    }

    private sealed class RecordingAdapter(
        MigrationPhase phase,
        bool succeeds = true,
        Action<PhaseExecutionContext>? work = null,
        bool returnsArtifactOnFailure = false) : IPhaseAdapter
    {
        public int Invocations { get; private set; }

        public MigrationPhase Phase => phase;

        public Task<PhaseExecutionResult> ExecuteAsync(PhaseExecutionContext context, CancellationToken cancellationToken)
        {
            Invocations++;
            work?.Invoke(context);

            ArtifactReference artifact = new(
                $"{context.OutputRoot}/reports/{phase}.json",
                ArtifactKind.ValidationReport,
                "Recorded by the test adapter.");

            return Task.FromResult(succeeds
                ? PhaseExecutionResult.Success([artifact])
                : new PhaseExecutionResult(
                    false,
                    returnsArtifactOnFailure ? [artifact] : [],
                    [],
                    "The test adapter failed on purpose."));
        }
    }

    private static PhaseOutcome Outcome(MigrationExecutionResult result, MigrationPhase phase) =>
        result.Phases.Single(outcome => outcome.Phase == phase);

    /// <summary>Answers every phase gate the same way, and records what it was asked.</summary>
    private sealed class StubMutationAuthorizer(bool authorized) : IPhaseMutationAuthorizer
    {
        public List<MutationAuthorizationRequest> Asked { get; } = [];

        public Task<MutationAuthorizationResult> AuthorizeAsync(
            MutationAuthorizationRequest request,
            CancellationToken cancellationToken)
        {
            Asked.Add(request);
            return Task.FromResult(authorized
                ? MutationAuthorizationResult.Allow("A grant covers this run.")
                : MutationAuthorizationResult.Deny("The grant for this run was revoked after it was queued."));
        }
    }

    /// <summary>
    /// Reading the approved target is a side effect that leaves the session workspace, so the executor
    /// re-asks the grant immediately before the phase runs. A grant revoked after the run was queued stops
    /// the connection here, before the adapter is ever constructed with a target to open.
    /// </summary>
    [Fact]
    public async Task A_target_read_the_server_will_not_authorize_never_reaches_its_adapter()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();
        RecordingAdapter adapter = new(MigrationPhase.TargetContractVerification);
        StubMutationAuthorizer authorizer = new(authorized: false);

        MigrationExecutionResult result = await new MigrationExecutor(workspace.Root, [adapter], authorizer)
            .ExecuteAsync(
                Request(mode: ExecutionMode.SandboxMigration, executionApproval: Requests.Approved("release@contoso.com")),
                Operator);

        PhaseOutcome outcome = Outcome(result, MigrationPhase.TargetContractVerification);
        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Equal(0, adapter.Invocations);
        Assert.Contains("revoked after it was queued", outcome.Detail!, StringComparison.Ordinal);
        Assert.Contains("Nothing was read", outcome.Detail!, StringComparison.Ordinal);

        Assert.Contains(
            authorizer.Asked,
            asked => asked.Phase == MigrationPhase.TargetContractVerification &&
                asked.Mutation == MutationClass.ExternalTargetRead);
    }

    /// <summary>
    /// The phase boundary the deployment below it depends on. A verification the server will not record is
    /// a verification nothing attributes to a decision, so the phase ends Failed and the deployment adapter
    /// sees a run with no per-entry evidence rather than publishing on the strength of an unrecorded read.
    /// </summary>
    [Fact]
    public async Task A_verification_the_server_will_not_record_fails_that_phase_before_deployment()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();
        RecordingAdapter verification = new(MigrationPhase.TargetContractVerification);
        List<MigrationPhase> observed = [];

        MigrationExecutionResult result = await new MigrationExecutor(
                workspace.Root,
                [verification],
                phaseObserver: (outcome, _) =>
                {
                    observed.Add(outcome.Phase);
                    return Task.FromResult<string?>(
                        outcome.Phase == MigrationPhase.TargetContractVerification
                            ? "That run's verification record describes no executed case, so nothing was entered."
                            : null);
                })
            .ExecuteAsync(
                Request(mode: ExecutionMode.SandboxMigration, executionApproval: Requests.Approved("release@contoso.com")),
                Operator);

        PhaseOutcome outcome = Outcome(result, MigrationPhase.TargetContractVerification);
        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("no executed case", outcome.Detail!, StringComparison.Ordinal);

        // Recorded at the moment the phase finished, not after the run reached a terminal state.
        Assert.Contains(MigrationPhase.TargetContractVerification, observed);
        Assert.True(
            MigrationLifecycle.PositionOf(MigrationPhase.TargetContractVerification)
                < MigrationLifecycle.PositionOf(MigrationPhase.TargetApplicationDeployment));
    }

    [Fact]
    public async Task A_failed_phase_retains_its_diagnostic_artifacts_without_attesting()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();
        RecordingAdapter adapter = new(
            MigrationPhase.SandboxDataMigration,
            succeeds: false,
            returnsArtifactOnFailure: true);

        MigrationExecutionResult result = await new MigrationExecutor(workspace.Root, [adapter])
            .ExecuteAsync(
                Request(mode: ExecutionMode.SandboxMigration, executionApproval: Requests.Approved("release@contoso.com")),
                Operator);

        ArtifactReference artifact = Assert.Single(Outcome(result, MigrationPhase.SandboxDataMigration).Artifacts);
        Assert.Contains(artifact, result.Artifacts);
        Assert.Empty(result.Attestations);
    }

    [Fact]
    public async Task A_phase_the_planner_blocks_is_never_executed()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        // The schema export is the one input a schema conversion cannot proceed without.
        IReadOnlyList<EvidenceItem> incomplete = [.. FullEvidence().Where(item => item.Kind != EvidenceKind.DatabaseSchemaExport)];
        RecordingAdapter adapter = new(MigrationPhase.DatabaseConversion);

        MigrationExecutor executor = new(workspace.Root, [adapter]);
        MigrationExecutionResult result = await executor.ExecuteAsync(Request(evidence: incomplete), Operator);

        Assert.Equal(0, adapter.Invocations);

        PhaseOutcome outcome = Outcome(result, MigrationPhase.DatabaseConversion);
        Assert.Equal(PhaseStatus.BlockedOnEvidence, outcome.PlannedStatus);
        Assert.Equal(PhaseExecutionState.SkippedByPlanner, outcome.State);
        Assert.Empty(outcome.Artifacts);
        Assert.Empty(result.Attestations);
        Assert.False(workspace.Exists("out/orders/database/postgresql/schema/schema.sql"));
    }

    [Fact]
    public async Task A_missing_test_baseline_does_not_block_a_schema_conversion()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        // Emitting DDL into the workspace asserts nothing about behaviour, so it does not need a baseline.
        IReadOnlyList<EvidenceItem> noBaseline = [.. FullEvidence().Where(item => item.Kind != EvidenceKind.TestBaseline)];
        RecordingAdapter adapter = new(MigrationPhase.DatabaseConversion);

        MigrationExecutionResult result = await new MigrationExecutor(workspace.Root, [adapter])
            .ExecuteAsync(Request(evidence: noBaseline), Operator);

        Assert.Equal(PhaseStatus.Planned, Outcome(result, MigrationPhase.DatabaseConversion).PlannedStatus);
        Assert.Equal(1, adapter.Invocations);
    }

    [Fact]
    public async Task A_missing_test_baseline_still_blocks_converting_application_code()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        IReadOnlyList<EvidenceItem> noBaseline = [.. FullEvidence().Where(item => item.Kind != EvidenceKind.TestBaseline)];
        RecordingAdapter adapter = new(MigrationPhase.ApplicationCodeConversion);

        MigrationExecutionResult result = await new MigrationExecutor(workspace.Root, [adapter])
            .ExecuteAsync(Request(evidence: noBaseline), Operator);

        Assert.Equal(PhaseStatus.BlockedOnEvidence, Outcome(result, MigrationPhase.ApplicationCodeConversion).PlannedStatus);
        Assert.Equal(0, adapter.Invocations);
    }

    [Fact]
    public async Task A_missing_forms_module_does_not_block_a_schema_conversion()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        // The defect this guards: an Oracle schema conversion refused for want of Forms binaries it never reads.
        IReadOnlyList<EvidenceItem> noForms =
            [.. FullEvidence().Where(item => item.Kind is not (EvidenceKind.FormsModuleSource or EvidenceKind.FormsXmlExport))];
        RecordingAdapter adapter = new(MigrationPhase.DatabaseConversion);

        MigrationExecutionResult result = await new MigrationExecutor(workspace.Root, [adapter])
            .ExecuteAsync(Request(evidence: noForms), Operator);

        Assert.Equal(PhaseStatus.Planned, Outcome(result, MigrationPhase.DatabaseConversion).PlannedStatus);
        Assert.Equal(1, adapter.Invocations);
    }

    /// <summary>
    /// The application converter reads the same tree the normalization gate just refused, so without an
    /// explicit dependency it would happily generate screens from table structure and present them as a
    /// migration of modules nobody opened.
    /// </summary>
    [Fact]
    public async Task A_failed_normalization_blocks_application_conversion_and_generates_nothing()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        MigrationExecutionResult result = await new MigrationExecutor(
                workspace.Root,
                [new SourceNormalizationAdapter(), new ApplicationCodeConversionAdapter()])
            .ExecuteAsync(Request(), Operator);

        Assert.Equal(PhaseExecutionState.Failed, Outcome(result, MigrationPhase.SourceNormalization).State);

        PhaseOutcome application = Outcome(result, MigrationPhase.ApplicationCodeConversion);
        Assert.Equal(PhaseExecutionState.BlockedByDependency, application.State);
        Assert.Contains("SourceNormalization", application.Detail!, StringComparison.Ordinal);
        Assert.Empty(application.Artifacts);

        Assert.False(workspace.Exists("out/orders/application/CONVERSION_NOTES.md"));
        Assert.False(Directory.Exists(workspace.Absolute("out/orders/application")));
    }

    [Fact]
    public async Task A_declared_release_that_contradicts_the_run_blocks_application_conversion()
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/001_schema.sql", OracleSamples.Schema);
        workspace.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", OracleSamples.FormsXml("12.2.1.4"));

        MigrationRunRequest request = Request() with { OracleFormsVersion = "6i" };

        MigrationExecutionResult result = await new MigrationExecutor(
                workspace.Root,
                [new SourceNormalizationAdapter(), new ApplicationCodeConversionAdapter()])
            .ExecuteAsync(request, Operator);

        Assert.Equal(PhaseExecutionState.Failed, Outcome(result, MigrationPhase.SourceNormalization).State);
        Assert.Equal(PhaseExecutionState.BlockedByDependency, Outcome(result, MigrationPhase.ApplicationCodeConversion).State);
        Assert.False(Directory.Exists(workspace.Absolute("out/orders/application")));
    }

    [Fact]
    public async Task A_failed_forms_normalization_does_not_stop_the_database_conversion()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        MigrationExecutionResult result = await new MigrationExecutor(
                workspace.Root,
                [new SourceNormalizationAdapter(), new DatabaseConversionAdapter()])
            .ExecuteAsync(Request(), Operator);

        Assert.Equal(PhaseExecutionState.Failed, Outcome(result, MigrationPhase.SourceNormalization).State);
        Assert.Equal(PhaseExecutionState.Executed, Outcome(result, MigrationPhase.DatabaseConversion).State);
        Assert.True(workspace.Exists("out/orders/database/postgresql/schema/schema.sql"));
    }

    /// <summary>
    /// A phase nobody ran refused nothing and confirmed nothing. Letting the conversion proceed on that
    /// basis is how a run with Forms source it never opened produced screens from table structure.
    /// </summary>
    [Fact]
    public async Task An_unregistered_normalization_adapter_blocks_application_conversion_when_forms_source_exists()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        RecordingAdapter application = new(MigrationPhase.ApplicationCodeConversion);

        MigrationExecutionResult result = await new MigrationExecutor(workspace.Root, [application])
            .ExecuteAsync(Request(), Operator);

        Assert.Equal(PhaseExecutionState.AdapterNotImplemented, Outcome(result, MigrationPhase.SourceNormalization).State);

        PhaseOutcome outcome = Outcome(result, MigrationPhase.ApplicationCodeConversion);
        Assert.Equal(PhaseExecutionState.BlockedByDependency, outcome.State);
        Assert.Contains("no adapter is registered for it", outcome.Detail!, StringComparison.Ordinal);
        Assert.Equal(0, application.Invocations);
    }

    /// <summary>
    /// A database-only estate has no Forms source for normalization to adjudicate, so requiring the phase
    /// there would refuse a schema conversion that opens no Forms file.
    /// </summary>
    [Fact]
    public async Task A_database_only_run_converts_the_application_without_source_normalization()
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/001_schema.sql", OracleSamples.Schema);

        IReadOnlyList<EvidenceItem> noForms =
            [.. FullEvidence().Where(item => item.Kind is not (EvidenceKind.FormsModuleSource or EvidenceKind.FormsXmlExport))];

        RecordingAdapter application = new(MigrationPhase.ApplicationCodeConversion);

        MigrationExecutionResult result = await new MigrationExecutor(workspace.Root, [application])
            .ExecuteAsync(Request(evidence: noForms), Operator);

        Assert.Equal(PhaseExecutionState.SkippedByPlanner, Outcome(result, MigrationPhase.SourceNormalization).State);
        Assert.Equal(PhaseExecutionState.Executed, Outcome(result, MigrationPhase.ApplicationCodeConversion).State);
        Assert.Equal(1, application.Invocations);
    }

    [Fact]
    public async Task A_dependency_block_cascades_to_the_phases_downstream_of_it()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        RecordingAdapter build = new(MigrationPhase.BuildAndStaticValidation);

        MigrationExecutionResult result = await new MigrationExecutor(
                workspace.Root,
                [new SourceNormalizationAdapter(), new ApplicationCodeConversionAdapter(), build])
            .ExecuteAsync(Request(), Operator);

        Assert.Equal(PhaseExecutionState.BlockedByDependency, Outcome(result, MigrationPhase.ApplicationCodeConversion).State);
        Assert.Equal(PhaseExecutionState.BlockedByDependency, Outcome(result, MigrationPhase.BuildAndStaticValidation).State);
        Assert.Equal(0, build.Invocations);
    }

    /// <summary>
    /// Once normalization has adjudicated the estate, its intermediate representation is the only Forms
    /// model the converter may read. Re-parsing the original export would let the two phases reach
    /// different conclusions about the same tree.
    /// </summary>
    [Fact]
    public async Task Application_conversion_reads_the_normalized_representation_rather_than_the_original_export()
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/001_schema.sql", OracleSamples.Schema);
        workspace.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", OracleSamples.FormsXml("12.2.1.4"));

        MigrationExecutionResult result = await new MigrationExecutor(
                workspace.Root,
                [new SourceNormalizationAdapter(), new ApplicationCodeConversionAdapter()])
            .ExecuteAsync(Request() with { OracleFormsVersion = "12c" }, Operator);

        Assert.Equal(PhaseExecutionState.Executed, Outcome(result, MigrationPhase.SourceNormalization).State);
        Assert.Equal(PhaseExecutionState.Executed, Outcome(result, MigrationPhase.ApplicationCodeConversion).State);

        Assert.Contains(result.Progress, entry =>
            entry.Text.Contains("normalized Forms module(s) from out/orders/intermediate/forms-ir.json", StringComparison.Ordinal)
            && entry.Text.Contains("original exports were not re-read", StringComparison.Ordinal));

        // The module reached the generated application through the IR, not through a second parse.
        Assert.Contains("**1 Forms module(s) were read from an XML export.**", workspace.Read("out/orders/application/CONVERSION_NOTES.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_corrupt_normalized_representation_refuses_rather_than_generating_from_the_schema()
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/001_schema.sql", OracleSamples.Schema);
        workspace.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", OracleSamples.FormsXml("12.2.1.4"));
        workspace.WriteFile("out/orders/intermediate/forms-ir.json", "{ \"modules\": 7 }");

        MigrationRunRequest request = Request() with { OracleFormsVersion = "12c" };
        PhasePlan plan = MigrationRunPlanner.Plan(request).Phases.Single(phase => phase.Phase == MigrationPhase.ApplicationCodeConversion);

        PhaseExecutionResult result = await new ApplicationCodeConversionAdapter().ExecuteAsync(
            new PhaseExecutionContext(workspace.Root, request.SourceRoot, request.OutputRoot, plan, request, (_, _) => { })
            {
                CompletedPhases =
                [
                    new PhaseOutcome(MigrationPhase.SourceNormalization, PhaseStatus.Planned, PhaseExecutionState.Executed, [], [], null),
                ],
            },
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("was refused", result.FailureReason!, StringComparison.Ordinal);
        Assert.False(workspace.Exists("out/orders/application/CONVERSION_NOTES.md"));
    }

    [Fact]
    public void An_approved_sandbox_run_is_authorized_without_forms_evidence()
    {
        IReadOnlyList<EvidenceItem> noForms =
            [.. FullEvidence().Where(item => item.Kind is not (EvidenceKind.FormsModuleSource or EvidenceKind.FormsXmlExport))];

        MigrationRunPlan plan = MigrationRunPlanner.Plan(Request(
            mode: ExecutionMode.SandboxMigration,
            evidence: noForms,
            executionApproval: Requests.Approved("release-manager@contoso.com")));

        // Loading rows into PostgreSQL opens no .fmb, so requiring one would only invite a false tick.
        Assert.Equal(ExecutionMode.SandboxMigration, plan.AuthorizedMode);
        Assert.Equal(
            PhaseStatus.Planned,
            plan.Phases.Single(phase => phase.Phase == MigrationPhase.SandboxDataMigration).Status);
    }

    [Fact]
    public void A_sandbox_run_without_its_own_approval_is_still_refused()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Request(
            mode: ExecutionMode.SandboxMigration,
            evidence: FullEvidence()));

        Assert.Equal(
            PhaseStatus.BlockedOnApproval,
            plan.Phases.Single(phase => phase.Phase == MigrationPhase.SandboxDataMigration).Status);
    }

    [Theory]
    [InlineData(MigrationPhase.DatabaseConversion)]
    [InlineData(MigrationPhase.ApplicationCodeConversion)]
    [InlineData(MigrationPhase.SandboxDataMigration)]
    [InlineData(MigrationPhase.DataReconciliation)]
    public void No_phase_that_ignores_forms_modules_is_gated_on_them(MigrationPhase phase)
    {
        IReadOnlyList<EvidenceItem> noForms =
            [.. FullEvidence().Where(item => item.Kind is not (EvidenceKind.FormsModuleSource or EvidenceKind.FormsXmlExport))];

        MigrationRunPlan plan = MigrationRunPlanner.Plan(Request(
            mode: ExecutionMode.ProductionCutover,
            evidence: noForms,
            executionApproval: Requests.Approved("release-manager@contoso.com")));

        PhasePlan resolved = plan.Phases.Single(candidate => candidate.Phase == phase);

        Assert.DoesNotContain(
            resolved.Blockers,
            blocker => blocker.Contains("FormsModuleSource", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_planned_phase_with_an_adapter_runs_and_writes_the_declared_artifacts()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        MigrationExecutor executor = new(workspace.Root, [new SourceAnalysisAdapter()]);
        MigrationExecutionResult result = await executor.ExecuteAsync(Request(), Operator);

        PhaseOutcome outcome = Outcome(result, MigrationPhase.SourceAnalysis);
        Assert.Equal(PhaseStatus.Planned, outcome.PlannedStatus);
        Assert.Equal(PhaseExecutionState.Executed, outcome.State);

        string[] expected =
        [
            "out/orders/analysis/dependency-graph.json",
            "out/orders/analysis/APPLICATION_INVENTORY.md",
            "out/orders/analysis/DATA_DICTIONARY.md",
            "out/orders/analysis/DEPENDENCY_MAP.md",
            "out/orders/analysis/TECHNICAL_DEBT_REPORT.md",
        ];

        foreach (string path in expected)
        {
            Assert.True(workspace.Exists(path), $"{path} was not written.");
            Assert.Contains(outcome.Artifacts, artifact => artifact.Path == path);
        }

        Assert.All(result.Artifacts, artifact => Assert.Null(WorkspacePath.Validate(artifact.Path, "artifact")));
    }

    [Fact]
    public async Task Source_analysis_reports_binary_modules_without_decoding_them()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        MigrationExecutor executor = new(workspace.Root, [new SourceAnalysisAdapter()]);
        await executor.ExecuteAsync(Request(), Operator);

        string inventory = workspace.Read("out/orders/analysis/APPLICATION_INVENTORY.md");
        Assert.Contains("ACCOUNT_OPEN.fmb", inventory, StringComparison.Ordinal);
        Assert.Contains("Forms Builder", inventory, StringComparison.Ordinal);

        string debt = workspace.Read("out/orders/analysis/TECHNICAL_DEBT_REPORT.md");
        Assert.Contains("Modules that cannot be analysed statically", debt, StringComparison.Ordinal);

        string dictionary = workspace.Read("out/orders/analysis/DATA_DICTIONARY.md");
        Assert.Contains("BANK_TRANSACTION", dictionary, StringComparison.Ordinal);
        Assert.DoesNotContain("WHEN-BUTTON-PRESSED", dictionary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Technical_debt_findings_are_evidence_based()
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/schema.sql", """
            CREATE TABLE APP_USER
            (
                USER_ID        NUMBER(9) PRIMARY KEY,
                PASSWORD       VARCHAR2(40) NOT NULL,
                PASSWORD_HASH  RAW(32) NOT NULL
            );

            CREATE TABLE AUDIT_TRAIL
            (
                ENTRY_ID  NUMBER(9) NOT NULL,
                USER_ID   NUMBER(9) NOT NULL,
                CONSTRAINT AUDIT_USER_FK FOREIGN KEY (USER_ID) REFERENCES MISSING_USER (USER_ID)
            );
            """);

        MigrationExecutor executor = new(workspace.Root, [new SourceAnalysisAdapter()]);
        await executor.ExecuteAsync(Request(), Operator);

        string debt = workspace.Read("out/orders/analysis/TECHNICAL_DEBT_REPORT.md");
        Assert.Contains("APP_USER.PASSWORD is declared VARCHAR2(40)", debt, StringComparison.Ordinal);
        Assert.Contains("MISSING_USER", debt, StringComparison.Ordinal);
        Assert.Contains("AUDIT_TRAIL declares 2 columns and no PRIMARY KEY", debt, StringComparison.Ordinal);

        // A hashed credential column is not evidence of a plaintext credential, so it must not be flagged.
        Assert.DoesNotContain("APP_USER.PASSWORD_HASH is declared", debt, StringComparison.Ordinal);

        // No binary module exists in this source, so the binary-module finding must be absent.
        Assert.DoesNotContain("Modules that cannot be analysed statically", debt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Database_conversion_writes_postgresql_ddl_and_a_conversion_report()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        MigrationExecutor executor = new(workspace.Root, [new DatabaseConversionAdapter()]);
        MigrationExecutionResult result = await executor.ExecuteAsync(Request(), Operator);

        Assert.Equal(PhaseExecutionState.Executed, Outcome(result, MigrationPhase.DatabaseConversion).State);

        string ddl = workspace.Read("out/orders/database/postgresql/schema/schema.sql");
        Assert.Contains("CREATE TABLE bank_transaction (", ddl, StringComparison.Ordinal);
        Assert.Contains("amount numeric(12,2) NOT NULL", ddl, StringComparison.Ordinal);

        string report = workspace.Read("out/orders/database/postgresql/conversion-report.md");
        Assert.Contains("Manual rewrite required", report, StringComparison.Ordinal);
        Assert.Contains("PACKAGE", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Database_conversion_refuses_a_target_it_does_not_implement()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        MigrationExecutor executor = new(workspace.Root, [new DatabaseConversionAdapter()]);
        MigrationExecutionResult result = await executor.ExecuteAsync(
            Request(database: DatabaseTarget.AzureSqlDatabase), Operator);

        PhaseOutcome outcome = Outcome(result, MigrationPhase.DatabaseConversion);
        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("AzureSqlDatabase", outcome.Detail!, StringComparison.Ordinal);
        Assert.Empty(result.Attestations);
        Assert.False(Directory.Exists(workspace.Absolute("out/orders/database")));
    }

    [Fact]
    public async Task A_failed_phase_produces_no_artifacts_and_no_attestation()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();
        RecordingAdapter adapter = new(MigrationPhase.DataReconciliation, succeeds: false);

        MigrationExecutor executor = new(workspace.Root, [adapter]);
        MigrationExecutionResult result = await executor.ExecuteAsync(
            Request(mode: ExecutionMode.SandboxMigration, executionApproval: Requests.Approved("release-manager@contoso.com")),
            Operator);

        Assert.Equal(1, adapter.Invocations);
        Assert.Equal(PhaseExecutionState.Failed, Outcome(result, MigrationPhase.DataReconciliation).State);
        Assert.Empty(result.Artifacts);
        Assert.Empty(result.Attestations);
    }

    [Fact]
    public async Task A_successful_phase_is_attested_by_the_supplied_operator_and_cites_what_it_wrote()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();
        RecordingAdapter adapter = new(MigrationPhase.DataReconciliation);

        MigrationExecutor executor = new(workspace.Root, [adapter]);
        MigrationExecutionResult result = await executor.ExecuteAsync(
            Request(mode: ExecutionMode.SandboxMigration, executionApproval: Requests.Approved("release-manager@contoso.com")),
            Operator);

        MigrationAttestation attestation = Assert.Single(result.Attestations);
        Assert.Equal(AttestationKind.DataReconciliationPassed, attestation.Kind);
        Assert.True(attestation.Succeeded);
        Assert.Equal(Operator, attestation.AttestedBy);
        Assert.Equal(
            Outcome(result, MigrationPhase.DataReconciliation).Artifacts.Select(artifact => artifact.Path),
            attestation.Artifacts.Select(artifact => artifact.Path));
    }

    [Fact]
    public async Task Human_acceptance_is_never_attested_by_an_adapter()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();
        RecordingAdapter adapter = new(MigrationPhase.HumanAcceptance);

        MigrationExecutor executor = new(workspace.Root, [adapter]);
        MigrationExecutionResult result = await executor.ExecuteAsync(
            Request(mode: ExecutionMode.SandboxMigration, executionApproval: Requests.Approved("release-manager@contoso.com")),
            Operator);

        Assert.Equal(PhaseExecutionState.Executed, Outcome(result, MigrationPhase.HumanAcceptance).State);
        Assert.DoesNotContain(result.Attestations, attestation => attestation.Kind == AttestationKind.HumanAcceptanceSigned);
    }

    [Fact]
    public async Task Without_an_operator_identity_nothing_is_attested()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        MigrationExecutor executor = new(workspace.Root, [new RecordingAdapter(MigrationPhase.DataReconciliation)]);
        MigrationExecutionResult result = await executor.ExecuteAsync(
            Request(mode: ExecutionMode.SandboxMigration, executionApproval: Requests.Approved("release-manager@contoso.com")),
            operatorIdentity: "   ");

        Assert.Equal(PhaseExecutionState.Executed, Outcome(result, MigrationPhase.DataReconciliation).State);
        Assert.Empty(result.Attestations);
    }

    [Fact]
    public async Task A_planned_phase_without_an_adapter_is_reported_rather_than_failing_the_run()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        MigrationExecutor executor = new(workspace.Root, [new SourceAnalysisAdapter()]);
        MigrationExecutionResult result = await executor.ExecuteAsync(Request(), Operator);

        PhaseOutcome outcome = Outcome(result, MigrationPhase.ApplicationCodeConversion);
        Assert.Equal(PhaseStatus.Planned, outcome.PlannedStatus);
        Assert.Equal(PhaseExecutionState.AdapterNotImplemented, outcome.State);
        Assert.Empty(outcome.Artifacts);
        Assert.Equal(PhaseExecutionState.Executed, Outcome(result, MigrationPhase.SourceAnalysis).State);
    }

    [Fact]
    public async Task An_adapter_that_writes_outside_the_workspace_fails_the_phase_and_writes_nothing()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();
        string escape = Path.Combine(Path.GetDirectoryName(workspace.Root.TrimEnd(Path.DirectorySeparatorChar))!, "escape.txt");

        RecordingAdapter adapter = new(
            MigrationPhase.DatabaseConversion,
            work: context => context.Workspace.WriteText("../escape.txt", "payload"));

        MigrationExecutor executor = new(workspace.Root, [adapter]);
        MigrationExecutionResult result = await executor.ExecuteAsync(Request(), Operator);

        PhaseOutcome outcome = Outcome(result, MigrationPhase.DatabaseConversion);
        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Empty(result.Artifacts);
        Assert.Empty(result.Attestations);
        Assert.False(File.Exists(escape));
    }

    [Fact]
    public async Task Phases_run_in_lifecycle_order_and_progress_is_reported()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        List<ExecutionProgress> streamed = [];
        MigrationExecutor executor = new(workspace.Root, [new SourceAnalysisAdapter(), new DatabaseConversionAdapter()]);
        MigrationExecutionResult result = await executor.ExecuteAsync(Request(), Operator, streamed.Add);

        // MigrationPhase numbers are a frozen identity scheme, so lifecycle order is stated separately.
        Assert.Equal(
            result.Phases.Select(outcome => outcome.Phase).OrderBy(MigrationLifecycle.PositionOf),
            result.Phases.Select(outcome => outcome.Phase));
        Assert.Equal(result.Progress.Count, streamed.Count);
        Assert.Contains(streamed, entry => entry.Level == "done" && entry.Text.Contains("SourceAnalysis", StringComparison.Ordinal));

        List<MigrationPhase> order = [.. result.Phases.Select(outcome => outcome.Phase)];
        Assert.True(order.IndexOf(MigrationPhase.SourceAnalysis) < order.IndexOf(MigrationPhase.DatabaseConversion));
    }
}
