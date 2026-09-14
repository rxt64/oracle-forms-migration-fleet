// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Agents;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Fleet.Execution.Adapters;

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
    public void A_dollar_quoted_body_is_kept_whole_rather_than_split_on_its_semicolons()
    {
        // A PL/pgSQL body is full of semicolons; splitting inside one yields fragments that each fail.
        const string schema = """
            CREATE TABLE t (id bigint);
            CREATE OR REPLACE FUNCTION f() RETURNS integer
            AS $legacy$
            DECLARE
                v integer;
            BEGIN
                SELECT 1 INTO v;
                RETURN v;
            END;
            $legacy$ LANGUAGE plpgsql;
            """;

        IReadOnlyList<string> statements = DataMigrationTranslator.SplitSchema(schema);

        Assert.Equal(2, statements.Count);
        Assert.Contains("RETURN v;", statements[1], StringComparison.Ordinal);
        Assert.EndsWith("LANGUAGE plpgsql", statements[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_semicolon_in_a_line_comment_does_not_end_the_statement()
    {
        IReadOnlyList<string> statements = DataMigrationTranslator.SplitSchema(
            "CREATE TABLE t ( -- id; name;\n id bigint);");

        Assert.Single(statements);
    }

    [Fact]
    public void A_terminator_less_client_directive_does_not_swallow_the_next_row()
    {
        // SET DEFINE OFF carries no semicolon, so the splitter joined it to the INSERT that followed and
        // the row was dropped without a failure. A load that quietly loses a record is the worst outcome.
        const string script = """
            SET DEFINE OFF

            INSERT INTO bank_account_request (request_id) VALUES (1001);
            INSERT INTO bank_account_request (request_id) VALUES (1002);
            """;

        IReadOnlyList<DataMigrationStatement> statements = Translate(script);

        Assert.Equal(2, statements.Count);
        Assert.Contains(statements, statement => statement.Sql.Contains("1001", StringComparison.Ordinal));
    }

    [Fact]
    public void The_set_clause_of_an_update_is_not_mistaken_for_a_client_directive()
    {
        const string script = """
            UPDATE bank_account
            SET status = 'N'
            WHERE account_id = 1;
            INSERT INTO bank_account (account_id) VALUES (2);
            """;

        DataMigrationStatement statement = Assert.Single(Translate(script));

        Assert.Contains("bank_account", statement.Table, StringComparison.Ordinal);
        Assert.Contains("VALUES (2)", statement.Sql, StringComparison.Ordinal);
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

    public List<IReadOnlyList<string>> PreparedBatches { get; } = [];

    public Queue<SchemaDeploymentOutcome> PrepareOutcomes { get; } = [];

    public SchemaDeploymentOutcome PrepareOutcome { get; set; } = new(0, 0, []);

    /// <summary>What the target reports when reconciliation reads it back.</summary>
    public IReadOnlyList<TableRowCount> Counts { get; set; } = [];

    /// <summary>Columns and rows the target returns, per table.</summary>
    public Dictionary<string, (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows)> Fetched { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<IReadOnlyList<string?>>> FetchAsync(
        string table,
        IReadOnlyList<string> columns,
        int maxRows,
        CancellationToken cancellationToken)
    {
        if (!Fetched.TryGetValue(table, out var stored))
        {
            return Task.FromResult<IReadOnlyList<IReadOnlyList<string?>>>([]);
        }

        // Project the stored rows onto the columns the caller asked for, as the real gateway does.
        List<IReadOnlyList<string?>> projected = [];
        foreach (IReadOnlyList<string?> row in stored.Rows)
        {
            string?[] cells = new string?[columns.Count];
            for (int index = 0; index < columns.Count; index++)
            {
                int source = -1;
                for (int candidate = 0; candidate < stored.Columns.Count; candidate++)
                {
                    if (string.Equals(stored.Columns[candidate], columns[index], StringComparison.OrdinalIgnoreCase))
                    {
                        source = candidate;
                        break;
                    }
                }

                cells[index] = source >= 0 ? row[source] : null;
            }

            projected.Add(cells);
        }

        return Task.FromResult<IReadOnlyList<IReadOnlyList<string?>>>(projected);
    }

    public Task<IReadOnlyList<TableRowCount>> CountAsync(
        IReadOnlyList<string> tables,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TableRowCount>>(
            [.. tables.Select(table => Counts.FirstOrDefault(count => count.Table == table) ?? new TableRowCount(table, 0))]);

    public Task<SchemaDeploymentOutcome> PrepareAsync(
        IReadOnlyList<string> statements,
        CancellationToken cancellationToken)
    {
        Prepared = statements;
        PreparedBatches.Add(statements);
        return Task.FromResult(PrepareOutcomes.TryDequeue(out SchemaDeploymentOutcome? next) ? next : PrepareOutcome);
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

public class ProgramUnitRepairLoopTests
{
    private const string Broken = "CREATE OR REPLACE FUNCTION hr.raise_salary() RETURNS void AS $body$ BEGIN broken; END; $body$ LANGUAGE plpgsql";
    private const string Fixed = "CREATE OR REPLACE FUNCTION hr.raise_salary() RETURNS void AS $body$ BEGIN NULL; END; $body$ LANGUAGE plpgsql";

    private static SchemaDeploymentOutcome Rejected(string statement = Broken, string diagnostic = "42703 broken does not exist") =>
        new(0, 0, [diagnostic])
        {
            StatementFailures = [new SchemaStatementFailure(statement, diagnostic)],
        };

    [Fact]
    public async Task A_repair_is_accepted_only_after_the_target_compiles_it()
    {
        StubDataGateway gateway = new(new DataMigrationOutcome(0, 0, [], []));
        gateway.PrepareOutcomes.Enqueue(new SchemaDeploymentOutcome(1, 0, []));
        ScriptedAgent repairer = new(
            FleetRole.DatabaseConverter,
            "repair",
            new FleetAgentResult(true, Fixed, [], "fixed"));

        ProgramUnitRepairOutcome result = await new ProgramUnitRepairLoop(repairer)
            .RunAsync("HRMS", gateway, Rejected(), null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(Fixed, Assert.Single(result.AcceptedStatements));
        Assert.Equal(Fixed, Assert.Single(Assert.Single(gateway.PreparedBatches)));
    }

    [Fact]
    public async Task A_repair_that_adds_a_non_routine_statement_is_never_executed()
    {
        StubDataGateway gateway = new(new DataMigrationOutcome(0, 0, [], []));
        ScriptedAgent repairer = new(
            FleetRole.DatabaseConverter,
            "repair",
            new FleetAgentResult(true, Fixed + "; CREATE TABLE injected (id bigint)", [], "expanded scope"));

        ProgramUnitRepairOutcome result = await new ProgramUnitRepairLoop(repairer)
            .RunAsync("HRMS", gateway, Rejected(), null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(gateway.PreparedBatches);
        Assert.Contains("non-routine", Assert.Single(result.OutstandingFailures), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repair_that_changes_the_parameter_signature_is_never_executed()
    {
        const string original = "CREATE OR REPLACE FUNCTION hr.raise_salary(employee_id bigint) RETURNS void AS $body$ BEGIN broken; END; $body$ LANGUAGE plpgsql";
        const string changed = "CREATE OR REPLACE FUNCTION hr.raise_salary(employee_id text) RETURNS void AS $body$ BEGIN NULL; END; $body$ LANGUAGE plpgsql";
        StubDataGateway gateway = new(new DataMigrationOutcome(0, 0, [], []));
        ScriptedAgent repairer = new(
            FleetRole.DatabaseConverter,
            "repair",
            new FleetAgentResult(true, changed, [], "changed signature"));

        ProgramUnitRepairOutcome result = await new ProgramUnitRepairLoop(repairer)
            .RunAsync("HRMS", gateway, Rejected(original), null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(gateway.PreparedBatches);
    }

    [Fact]
    public async Task A_dollar_tag_inside_a_default_string_cannot_hide_a_contract_change()
    {
        const string original = "CREATE OR REPLACE FUNCTION hr.raise_salary(note text DEFAULT 'value $body$ tail') RETURNS void AS $body$ BEGIN broken; END; $body$ LANGUAGE plpgsql";
        const string changed = "CREATE OR REPLACE FUNCTION hr.raise_salary(note text DEFAULT 'value $body$ changed') RETURNS text AS $body$ BEGIN RETURN 'x'; END; $body$ LANGUAGE plpgsql";
        StubDataGateway gateway = new(new DataMigrationOutcome(0, 0, [], []));
        ScriptedAgent repairer = new(
            FleetRole.DatabaseConverter,
            "repair",
            new FleetAgentResult(true, changed, [], "changed contract"));

        ProgramUnitRepairOutcome result = await new ProgramUnitRepairLoop(repairer)
            .RunAsync("HRMS", gateway, Rejected(original), null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(gateway.PreparedBatches);
    }

    [Fact]
    public async Task Compiler_failures_stop_at_the_repair_budget()
    {
        StubDataGateway gateway = new(new DataMigrationOutcome(0, 0, [], []));
        gateway.PrepareOutcomes.Enqueue(Rejected(Fixed, "42703 first retry failed"));
        gateway.PrepareOutcomes.Enqueue(Rejected(Fixed, "42703 second retry failed"));
        ScriptedAgent repairer = new(
            FleetRole.DatabaseConverter,
            "repair",
            new FleetAgentResult(true, Fixed, [], "still trying"));

        ProgramUnitRepairOutcome result = await new ProgramUnitRepairLoop(repairer, maxAttempts: 2)
            .RunAsync("HRMS", gateway, Rejected(), null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, repairer.Calls);
        Assert.Equal(2, gateway.PreparedBatches.Count);
        Assert.Contains("second retry failed", Assert.Single(result.OutstandingFailures), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unattributed_compiler_failure_accepts_no_statement()
    {
        StubDataGateway gateway = new(new DataMigrationOutcome(0, 0, [], []));
        gateway.PrepareOutcomes.Enqueue(new SchemaDeploymentOutcome(0, 0, ["connection lost after compilation"]));
        ScriptedAgent repairer = new(
            FleetRole.DatabaseConverter,
            "repair",
            new FleetAgentResult(true, Fixed, [], "fixed"));

        ProgramUnitRepairOutcome result = await new ProgramUnitRepairLoop(repairer)
            .RunAsync("HRMS", gateway, Rejected(), null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(result.AcceptedStatements);
        Assert.Contains("connection lost", Assert.Single(result.OutstandingFailures), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transient_rate_limit_uses_the_next_bounded_attempt()
    {
        StubDataGateway gateway = new(new DataMigrationOutcome(0, 0, [], []));
        gateway.PrepareOutcomes.Enqueue(new SchemaDeploymentOutcome(1, 0, []));
        ScriptedAgent repairer = new(
            FleetRole.DatabaseConverter,
            "repair",
            FleetAgentResult.Failed("HTTP 429 rate limit exceeded"),
            new FleetAgentResult(true, Fixed, [], "fixed"));

        ProgramUnitRepairOutcome result = await new ProgramUnitRepairLoop(repairer, maxAttempts: 2)
            .RunAsync("HRMS", gateway, Rejected(), null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, repairer.Calls);
    }

    [Fact]
    public async Task A_compiler_rejection_after_a_rate_limit_is_the_outstanding_failure()
    {
        StubDataGateway gateway = new(new DataMigrationOutcome(0, 0, [], []));
        gateway.PrepareOutcomes.Enqueue(Rejected(Fixed, "42703 final compiler rejection"));
        ScriptedAgent repairer = new(
            FleetRole.DatabaseConverter,
            "repair",
            FleetAgentResult.Failed("HTTP 429 rate limit exceeded"),
            new FleetAgentResult(true, Fixed, [], "fixed"));

        ProgramUnitRepairOutcome result = await new ProgramUnitRepairLoop(repairer, maxAttempts: 2)
            .RunAsync("HRMS", gateway, Rejected(), null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("final compiler rejection", Assert.Single(result.OutstandingFailures), StringComparison.Ordinal);
        Assert.DoesNotContain("429", result.OutstandingFailures[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mixed_attributed_and_unattributed_failures_accept_no_statement()
    {
        const string secondBroken = "CREATE OR REPLACE FUNCTION hr.end_employment() RETURNS void AS $body$ BEGIN broken; END; $body$ LANGUAGE plpgsql";
        const string secondFixed = "CREATE OR REPLACE FUNCTION hr.end_employment() RETURNS void AS $body$ BEGIN NULL; END; $body$ LANGUAGE plpgsql";
        const string diagnostic = "42703 first statement failed";
        StubDataGateway gateway = new(new DataMigrationOutcome(0, 0, [], []));
        gateway.PrepareOutcomes.Enqueue(new SchemaDeploymentOutcome(0, 0, [diagnostic, "connection lost"])
        {
            StatementFailures = [new SchemaStatementFailure(Fixed, diagnostic)],
        });
        ScriptedAgent repairer = new(
            FleetRole.DatabaseConverter,
            "repair",
            new FleetAgentResult(true, $"{Fixed};\n{secondFixed}", [], "fixed"));
        SchemaDeploymentOutcome rejected = new(0, 0, ["first", "second"])
        {
            StatementFailures =
            [
                new SchemaStatementFailure(Broken, "first"),
                new SchemaStatementFailure(secondBroken, "second"),
            ],
        };

        ProgramUnitRepairOutcome result = await new ProgramUnitRepairLoop(repairer)
            .RunAsync("HRMS", gateway, rejected, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(result.AcceptedStatements);
        Assert.Equal(2, result.OutstandingFailures.Count);
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

    private static Task<MigrationExecutionResult> RunSandboxOnlyAsync(
        TemporaryWorkspace workspace,
        IDataMigrationGateway gateway,
        ProgramUnitRepairLoop repair) =>
        new MigrationExecutor(workspace.Root, [new SandboxDataMigrationAdapter(gateway, repair)])
            .ExecuteAsync(Request(), Operator);

    private static void WriteRejectedProgramUnitSchema(TemporaryWorkspace workspace)
    {
        workspace.WriteFile(
            "out/orders/database/postgresql/schema/schema.sql",
            $"""
            CREATE TABLE orders (id bigint);
            {PlSqlTranslator.ProgramUnitsMarker}
            CREATE OR REPLACE FUNCTION hr.raise_salary() RETURNS void AS $body$ BEGIN broken; END; $body$ LANGUAGE plpgsql;
            """);
    }

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

    [Fact]
    public async Task A_compiler_accepted_repair_is_persisted_and_returned_as_an_artifact()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();
        WriteRejectedProgramUnitSchema(workspace);
        StubDataGateway gateway = new(new DataMigrationOutcome(2, 0, [], [new TableRowCount("orders", 2)]));
        gateway.PrepareOutcomes.Enqueue(new SchemaDeploymentOutcome(1, 0, []));
        gateway.PrepareOutcomes.Enqueue(ProgramUnitRepairLoopTestsRejected());
        gateway.PrepareOutcomes.Enqueue(new SchemaDeploymentOutcome(1, 0, []));
        ScriptedAgent repairer = new(
            FleetRole.DatabaseConverter,
            "repair",
            new FleetAgentResult(true, ProgramUnitRepairLoopTestsFixed(), [], "fixed"));

        MigrationExecutionResult result = await RunSandboxOnlyAsync(
            workspace,
            gateway,
            new ProgramUnitRepairLoop(repairer));

        PhaseOutcome outcome = Outcome(result);
        Assert.Equal(PhaseExecutionState.Executed, outcome.State);
        Assert.Contains(outcome.Artifacts, artifact => artifact.Path.EndsWith("program-unit-repairs.sql", StringComparison.Ordinal));
        Assert.Contains(outcome.Artifacts, artifact => artifact.Path.EndsWith("program-unit-repair-audit.md", StringComparison.Ordinal));
        Assert.Contains("BEGIN NULL", workspace.Read("out/orders/database/postgresql/schema/program-unit-repairs.sql"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outstanding_program_units_load_rows_but_prevent_the_sandbox_attestation()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();
        WriteRejectedProgramUnitSchema(workspace);
        StubDataGateway gateway = new(new DataMigrationOutcome(2, 0, [], [new TableRowCount("orders", 2)]));
        gateway.PrepareOutcomes.Enqueue(new SchemaDeploymentOutcome(1, 0, []));
        gateway.PrepareOutcomes.Enqueue(ProgramUnitRepairLoopTestsRejected());
        gateway.PrepareOutcomes.Enqueue(ProgramUnitRepairLoopTestsRejected(ProgramUnitRepairLoopTestsFixed()));
        ScriptedAgent repairer = new(
            FleetRole.DatabaseConverter,
            "repair",
            new FleetAgentResult(true, ProgramUnitRepairLoopTestsFixed(), [], "still broken"));

        MigrationExecutionResult result = await RunSandboxOnlyAsync(
            workspace,
            gateway,
            new ProgramUnitRepairLoop(repairer, maxAttempts: 1));

        Assert.Equal(PhaseExecutionState.Failed, Outcome(result).State);
        Assert.NotNull(gateway.Applied);
        Assert.Contains(Outcome(result).Artifacts, artifact => artifact.Path.EndsWith("migration-report.md", StringComparison.Ordinal));
        Assert.Contains(Outcome(result).Artifacts, artifact => artifact.Path.EndsWith("program-unit-repair-audit.md", StringComparison.Ordinal));
        Assert.All(Outcome(result).Artifacts, artifact => Assert.Contains(artifact, result.Artifacts));
        Assert.DoesNotContain(result.Attestations, attestation => attestation.Kind == AttestationKind.SandboxMigrationCompleted);
    }

    [Fact]
    public async Task A_persisted_repair_is_recompiled_without_calling_the_model_again()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();
        WriteRejectedProgramUnitSchema(workspace);
        workspace.WriteFile(
            "out/orders/database/postgresql/schema/program-unit-repairs.sql",
            ProgramUnitRepairLoopTestsFixed() + ";\n");
        StubDataGateway gateway = new(new DataMigrationOutcome(2, 0, [], [new TableRowCount("orders", 2)]));
        gateway.PrepareOutcomes.Enqueue(new SchemaDeploymentOutcome(1, 0, []));
        gateway.PrepareOutcomes.Enqueue(ProgramUnitRepairLoopTestsRejected());
        gateway.PrepareOutcomes.Enqueue(new SchemaDeploymentOutcome(1, 0, []));
        ScriptedAgent repairer = new(
            FleetRole.DatabaseConverter,
            "repair",
            FleetAgentResult.Failed("the model must not be called"));

        MigrationExecutionResult result = await RunSandboxOnlyAsync(
            workspace,
            gateway,
            new ProgramUnitRepairLoop(repairer));

        Assert.Equal(PhaseExecutionState.Executed, Outcome(result).State);
        Assert.Equal(0, repairer.Calls);
        Assert.Equal(3, gateway.PreparedBatches.Count);
    }

    [Fact]
    public async Task A_persisted_partial_repair_is_revalidated_and_merged_with_a_new_repair()
    {
        const string secondBroken = "CREATE OR REPLACE FUNCTION hr.end_employment() RETURNS void AS $body$ BEGIN broken; END; $body$ LANGUAGE plpgsql";
        const string secondFixed = "CREATE OR REPLACE FUNCTION hr.end_employment() RETURNS void AS $body$ BEGIN NULL; END; $body$ LANGUAGE plpgsql";
        using TemporaryWorkspace workspace = SeededWorkspace();
        workspace.WriteFile(
            "out/orders/database/postgresql/schema/schema.sql",
            $"""
            CREATE TABLE orders (id bigint);
            {PlSqlTranslator.ProgramUnitsMarker}
            {ProgramUnitRepairLoopTestsRejected().StatementFailures[0].Statement};
            {secondBroken};
            """);
        workspace.WriteFile(
            "out/orders/database/postgresql/schema/program-unit-repairs.sql",
            ProgramUnitRepairLoopTestsFixed() + ";\n");
        StubDataGateway gateway = new(new DataMigrationOutcome(2, 0, [], [new TableRowCount("orders", 2)]));
        gateway.PrepareOutcomes.Enqueue(new SchemaDeploymentOutcome(1, 0, []));
        gateway.PrepareOutcomes.Enqueue(new SchemaDeploymentOutcome(0, 0, ["first", "second"])
        {
            StatementFailures =
            [
                new SchemaStatementFailure(ProgramUnitRepairLoopTestsRejected().StatementFailures[0].Statement, "first"),
                new SchemaStatementFailure(secondBroken, "second"),
            ],
        });
        gateway.PrepareOutcomes.Enqueue(new SchemaDeploymentOutcome(1, 0, []));
        gateway.PrepareOutcomes.Enqueue(new SchemaDeploymentOutcome(1, 0, []));
        ScriptedAgent repairer = new(
            FleetRole.DatabaseConverter,
            "repair",
            new FleetAgentResult(true, secondFixed, [], "fixed remaining routine"));

        MigrationExecutionResult result = await RunSandboxOnlyAsync(
            workspace,
            gateway,
            new ProgramUnitRepairLoop(repairer));

        Assert.Equal(PhaseExecutionState.Executed, Outcome(result).State);
        Assert.Equal(1, repairer.Calls);
        string persisted = workspace.Read("out/orders/database/postgresql/schema/program-unit-repairs.sql");
        Assert.Contains("raise_salary", persisted, StringComparison.Ordinal);
        Assert.Contains("end_employment", persisted, StringComparison.Ordinal);
    }

    private static string ProgramUnitRepairLoopTestsFixed() =>
        "CREATE OR REPLACE FUNCTION hr.raise_salary() RETURNS void AS $body$ BEGIN NULL; END; $body$ LANGUAGE plpgsql";

    private static SchemaDeploymentOutcome ProgramUnitRepairLoopTestsRejected(string? statement = null)
    {
        string rejected = statement ??
            "CREATE OR REPLACE FUNCTION hr.raise_salary() RETURNS void AS $body$ BEGIN broken; END; $body$ LANGUAGE plpgsql";
        const string diagnostic = "42703 broken does not exist";
        return new SchemaDeploymentOutcome(0, 0, [diagnostic])
        {
            StatementFailures = [new SchemaStatementFailure(rejected, diagnostic)],
        };
    }
}
