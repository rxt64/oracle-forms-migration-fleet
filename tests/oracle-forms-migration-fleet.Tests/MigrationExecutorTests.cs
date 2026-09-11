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
        Action<PhaseExecutionContext>? work = null) : IPhaseAdapter
    {
        public int Invocations { get; private set; }

        public MigrationPhase Phase => phase;

        public Task<PhaseExecutionResult> ExecuteAsync(PhaseExecutionContext context, CancellationToken cancellationToken)
        {
            Invocations++;
            work?.Invoke(context);

            return Task.FromResult(succeeds
                ? PhaseExecutionResult.Success(
                    [new ArtifactReference($"{context.OutputRoot}/reports/{phase}.json", ArtifactKind.ValidationReport, "Recorded by the test adapter.")])
                : PhaseExecutionResult.Failure("The test adapter failed on purpose."));
        }
    }

    private static PhaseOutcome Outcome(MigrationExecutionResult result, MigrationPhase phase) =>
        result.Phases.Single(outcome => outcome.Phase == phase);

    [Fact]
    public async Task A_phase_the_planner_blocks_is_never_executed()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        // Without a verified TestBaseline the planner refuses to authorize artifact generation.
        IReadOnlyList<EvidenceItem> incomplete = [.. FullEvidence().Where(item => item.Kind != EvidenceKind.TestBaseline)];
        RecordingAdapter adapter = new(MigrationPhase.DatabaseConversion);

        MigrationExecutor executor = new(workspace.Root, [adapter]);
        MigrationExecutionResult result = await executor.ExecuteAsync(Request(evidence: incomplete), Operator);

        Assert.Equal(ExecutionMode.PlanOnly, result.Plan.AuthorizedMode);
        Assert.Equal(0, adapter.Invocations);

        PhaseOutcome outcome = Outcome(result, MigrationPhase.DatabaseConversion);
        Assert.Equal(PhaseStatus.BlockedOnEvidence, outcome.PlannedStatus);
        Assert.Equal(PhaseExecutionState.SkippedByPlanner, outcome.State);
        Assert.Empty(outcome.Artifacts);
        Assert.Empty(result.Attestations);
        Assert.False(workspace.Exists("out/orders/database/postgresql/schema/schema.sql"));
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

        Assert.Equal(result.Phases.Select(outcome => outcome.Phase).OrderBy(phase => phase), result.Phases.Select(outcome => outcome.Phase));
        Assert.Equal(result.Progress.Count, streamed.Count);
        Assert.Contains(streamed, entry => entry.Level == "done" && entry.Text.Contains("SourceAnalysis", StringComparison.Ordinal));

        List<MigrationPhase> order = [.. result.Phases.Select(outcome => outcome.Phase)];
        Assert.True(order.IndexOf(MigrationPhase.SourceAnalysis) < order.IndexOf(MigrationPhase.DatabaseConversion));
    }
}
