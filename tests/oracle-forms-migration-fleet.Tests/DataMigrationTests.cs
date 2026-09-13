// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Tests;

public class DataMigrationTranslatorTests
{
    private static IReadOnlyList<DataMigrationStatement> Translate(string script) =>
        DataMigrationTranslator.Translate(script, out _);

    [Fact]
    public void An_insert_is_translated_and_the_table_recorded()
    {
        DataMigrationStatement statement = Assert.Single(
            Translate("INSERT INTO BANK_ACCOUNT (ACCOUNT_ID) VALUES (1);"));

        Assert.Equal("bank_account", statement.Table);
        Assert.StartsWith("INSERT INTO BANK_ACCOUNT", statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(';', statement.Sql);
    }

    [Theory]
    [InlineData("TO_DATE('2026-01-01','YYYY-MM-DD')", "DATE '2026-01-01'")]
    [InlineData("TO_TIMESTAMP('2026-01-01 10:00:00','YYYY-MM-DD HH24:MI:SS')", "TIMESTAMP '2026-01-01 10:00:00'")]
    [InlineData("HEXTORAW('DEADBEEF')", "decode('DEADBEEF', 'hex')")]
    [InlineData("SYSTIMESTAMP", "now()")]
    [InlineData("SYSDATE", "CURRENT_DATE")]
    [InlineData("BANK_ACCOUNT_SEQ.NEXTVAL", "nextval('bank_account_seq')")]
    public void Oracle_value_syntax_is_rewritten(string oracle, string expected)
    {
        DataMigrationStatement statement = Assert.Single(
            Translate($"INSERT INTO T (C) VALUES ({oracle});"));

        Assert.Contains(expected, statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_inserts_are_translated_and_everything_else_is_reported()
    {
        IReadOnlyList<DataMigrationStatement> statements = DataMigrationTranslator.Translate(
            """
            CREATE TABLE t (a int);
            INSERT INTO t (a) VALUES (1);
            DELETE FROM t;
            DROP TABLE t;
            """,
            out IReadOnlyList<string> skipped);

        Assert.Single(statements);
        Assert.Equal(3, skipped.Count);
        Assert.Contains(skipped, text => text.StartsWith("DROP TABLE", StringComparison.Ordinal));
    }

    [Fact]
    public void A_semicolon_inside_a_value_does_not_split_the_statement()
    {
        DataMigrationStatement statement = Assert.Single(
            Translate("INSERT INTO t (note) VALUES ('one; two');"));

        Assert.Contains("'one; two'", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Values_are_passed_through_without_alteration()
    {
        DataMigrationStatement statement = Assert.Single(
            Translate("INSERT INTO t (name) VALUES ('O''Brien');"));

        Assert.Contains("'O''Brien'", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void An_insert_inside_a_plsql_body_is_not_treated_as_data()
    {
        // It references procedure parameters, so running it moves no row and fails on a name that is
        // not a column. Reporting that as a failed row makes a load look broken when nothing was lost.
        const string script = """
            CREATE OR REPLACE PACKAGE BODY legacy_api AS
              PROCEDURE open_account(p_account_id NUMBER) IS
              BEGIN
                INSERT INTO bank_account (account_id) VALUES (p_account_id);
              END;
            END legacy_api;
            /
            INSERT INTO bank_account (account_id) VALUES (500001);
            """;

        DataMigrationStatement statement = Assert.Single(Translate(script));

        Assert.Contains("500001", statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("p_account_id", statement.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Standard_hash_becomes_the_postgres_digest_of_the_same_bytes()
    {
        DataMigrationStatement statement = Assert.Single(
            Translate("INSERT INTO t (h) VALUES (STANDARD_HASH('demo1234', 'SHA256'));"));

        Assert.DoesNotContain("STANDARD_HASH", statement.Sql, StringComparison.OrdinalIgnoreCase);

        // Oracle returns RAW and the schema converter maps RAW to bytea, so the digest must not be
        // rendered as hex text: that is the right value in a type the column rejects.
        Assert.Contains("sha256(convert_to('demo1234', 'UTF8'))", statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("encode(", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrecognised_hash_algorithm_is_left_alone_rather_than_guessed()
    {
        DataMigrationStatement statement = Assert.Single(
            Translate("INSERT INTO t (h) VALUES (STANDARD_HASH('x', 'SHA3-256'));"));

        Assert.Contains("STANDARD_HASH", statement.Sql, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class StubDataGateway(DataMigrationOutcome outcome) : IDataMigrationGateway{
    public IReadOnlyList<DataMigrationStatement>? Applied { get; private set; }

    public IReadOnlyList<string>? Prepared { get; private set; }

    public SchemaDeploymentOutcome PrepareOutcome { get; set; } = new(0, 0, []);

    public Task<SchemaDeploymentOutcome> PrepareAsync(
        IReadOnlyList<string> statements,
        CancellationToken cancellationToken)
    {
        Prepared = statements;
        return Task.FromResult(PrepareOutcome);
    }

    public Task<DataMigrationOutcome> ApplyAsync(
        IReadOnlyList<DataMigrationStatement> statements,
        IReadOnlyList<string> tables,
        CancellationToken cancellationToken)
    {
        Applied = statements;
        return Task.FromResult(outcome);
    }
}

public class SandboxDataMigrationPhaseTests
{
    private const string Operator = "migration-operator@contoso.com";

    private static MigrationRunRequest Request(HumanApproval? execution = null) => new()
    {
        EngagementId = "ENG-DATA",
        ApplicationName = "ORDERS",
        RequestedMode = ExecutionMode.SandboxMigration,
        Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
        SourceRoot = "legacy/forms",
        OutputRoot = "out/orders",
        Evidence =
        [
            Requests.Evidence("EV-INV", EvidenceKind.FormsModuleInventory),
            Requests.Evidence("EV-SRC", EvidenceKind.FormsModuleSource),
            Requests.Evidence("EV-PLSQL", EvidenceKind.PlSqlProgramUnit),
            Requests.Evidence("EV-SCHEMA", EvidenceKind.DatabaseSchemaExport),
            Requests.Evidence("EV-TEST", EvidenceKind.TestBaseline),
            Requests.Evidence("EV-DATA", EvidenceKind.DataProfile),
        ],
        PlanApproval = Requests.Approved("plan-owner@contoso.com"),
        ExecutionApproval = execution ?? Requests.Approved("release-manager@contoso.com"),
    };

    private static TemporaryWorkspace SeededWorkspace()
    {
        TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/001_schema.sql", OracleSamples.Schema);
        workspace.WriteFile("legacy/forms/db/002_data.sql", "INSERT INTO ORDERS (ID) VALUES (1);\nINSERT INTO ORDERS (ID) VALUES (2);");
        return workspace;
    }

    private static Task<MigrationExecutionResult> RunAsync(
        TemporaryWorkspace workspace, IDataMigrationGateway? gateway, HumanApproval? execution = null) =>
        new MigrationExecutor(workspace.Root, MigrationExecutor.DefaultAdapters(null, gateway))
            .ExecuteAsync(Request(execution), Operator);

    private static PhaseOutcome Outcome(MigrationExecutionResult result) =>
        result.Phases.Single(phase => phase.Phase == MigrationPhase.SandboxDataMigration);

    [Fact]
    public async Task Rows_are_loaded_and_the_counts_come_back_from_the_target()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();
        StubDataGateway gateway = new(new DataMigrationOutcome(2, 0, [], [new TableRowCount("orders", 2)]));

        MigrationExecutionResult result = await RunAsync(workspace, gateway);

        Assert.Equal(PhaseExecutionState.Executed, Outcome(result).State);
        Assert.Equal(2, gateway.Applied!.Count);
        Assert.Contains("orders", workspace.Read("out/orders/data/migration-report.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_converted_schema_is_applied_before_rows_are_loaded()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();
        StubDataGateway gateway = new(new DataMigrationOutcome(2, 0, [], [new TableRowCount("orders", 2)]));

        MigrationExecutionResult result = await RunAsync(workspace, gateway);

        // The DDL comes from this run's own conversion phase. Without this the target has to be prepared
        // by hand, and a hand-prepared target is untracked.
        Assert.Equal(PhaseExecutionState.Executed, Outcome(result).State);
        Assert.NotEmpty(gateway.Prepared!);
        Assert.Contains(gateway.Prepared!, statement => statement.Contains("create table", StringComparison.OrdinalIgnoreCase));

        // A comment-only trailer would be an empty query, not a statement worth sending.
        Assert.DoesNotContain(
            gateway.Prepared!,
            statement => statement.Split('\n').All(line => line.Trim().Length == 0 || line.TrimStart().StartsWith("--", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_schema_that_will_not_apply_stops_the_load()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();
        workspace.WriteFile("out/orders/database/postgresql/schema/schema.sql", "create table orders (id bigint);");
        StubDataGateway gateway = new(new DataMigrationOutcome(2, 0, [], [new TableRowCount("orders", 2)]))
        {
            PrepareOutcome = new SchemaDeploymentOutcome(0, 0, ["42601 syntax error"]),
        };

        MigrationExecutionResult result = await RunAsync(workspace, gateway);

        Assert.Equal(PhaseExecutionState.Failed, Outcome(result).State);
        Assert.Null(gateway.Applied);
    }

    [Fact]
    public async Task Without_an_execution_approval_no_row_is_touched()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();
        StubDataGateway gateway = new(new DataMigrationOutcome(0, 0, [], []));

        MigrationExecutionResult result = await RunAsync(workspace, gateway, HumanApproval.Pending);

        Assert.NotEqual(PhaseExecutionState.Executed, Outcome(result).State);
        Assert.Null(gateway.Applied);
    }

    [Fact]
    public async Task Without_a_gateway_the_phase_fails_rather_than_claiming_a_migration()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        MigrationExecutionResult result = await RunAsync(workspace, gateway: null);

        PhaseOutcome outcome = Outcome(result);
        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("cannot reach a database", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_partial_load_is_reported_as_a_failure()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();
        StubDataGateway gateway = new(new DataMigrationOutcome(1, 1, ["orders: 23505 duplicate key"], [new TableRowCount("orders", 1)]));

        MigrationExecutionResult result = await RunAsync(workspace, gateway);

        PhaseOutcome outcome = Outcome(result);
        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("incomplete copy", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_successful_load_still_produces_no_reconciliation_claim()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();
        StubDataGateway gateway = new(new DataMigrationOutcome(2, 0, [], [new TableRowCount("orders", 2)]));

        await RunAsync(workspace, gateway);

        string report = workspace.Read("out/orders/data/migration-report.md");
        Assert.Contains("They are not a reconciliation", report, StringComparison.Ordinal);
    }
}
