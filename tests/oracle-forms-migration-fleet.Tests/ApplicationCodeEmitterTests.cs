// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Tests;

public class ApplicationCodeEmitterTests
{
    private static ApplicationConversion Convert(DatabaseTarget target = DatabaseTarget.PostgreSql) =>
        ApplicationCodeEmitter.Convert(OracleSchemaParser.Parse(OracleSamples.Schema), "ORDERS", target);

    private static string File(ApplicationConversion conversion, string endsWith) =>
        conversion.Files.Single(file => file.Path.EndsWith(endsWith, StringComparison.Ordinal)).Contents;

    [Fact]
    public void An_entity_repository_and_controller_are_generated_for_every_table()
    {
        ApplicationConversion conversion = Convert();
        int tables = OracleSchemaParser.Parse(OracleSamples.Schema).Tables.Count;

        Assert.Equal(tables, conversion.Files.Count(file => file.Path.Contains("/domain/", StringComparison.Ordinal)));
        Assert.Equal(tables, conversion.Files.Count(file => file.Path.Contains("/repository/", StringComparison.Ordinal)));
        Assert.Equal(tables, conversion.Files.Count(file => file.Path.Contains("/api/", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_backend_targets_postgresql_and_never_oracle()
    {
        string pom = File(Convert(), "pom.xml");

        Assert.Contains("postgresql", pom, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ojdbc", pom, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("oracle", pom, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_datasource_is_passwordless_and_carries_no_credential()
    {
        string yaml = File(Convert(), "application.yml");

        Assert.Contains("passwordless-enabled: true", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("password:", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_postgresql_target_generates_nothing_rather_than_the_wrong_driver()
    {
        ApplicationConversion conversion = Convert(DatabaseTarget.AzureSqlDatabase);

        Assert.Empty(conversion.Files);
        Assert.Contains(conversion.Findings, finding => finding.Severity == ConversionSeverity.Unsupported);
    }

    [Fact]
    public void Plsql_program_units_are_flagged_as_living_in_the_database_not_in_this_tier()
    {
        OracleSchema schema = OracleSchemaParser.Parse(OracleSamples.Schema + "\n" + OracleSamples.PlSql);
        ApplicationConversion conversion = ApplicationCodeEmitter.Convert(schema, "ORDERS", DatabaseTarget.PostgreSql);

        // The database conversion translates these, so calling them untranslated here would contradict it.
        ConversionFinding finding = Assert.Single(
            conversion.Findings, candidate => candidate.Category == "Server-side logic");

        Assert.Equal(ConversionSeverity.ManualReview, finding.Severity);
        Assert.Contains("does not call it yet", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_screen_follows_the_forms_block_when_an_export_was_read()
    {
        OracleSchema schema = OracleSchemaParser.Parse(OracleSamples.Schema);
        OracleTable table = schema.Tables[0];

        // Deliberately the reverse of the table's column order, so a pass can only come from the form.
        IReadOnlyList<FormsItem> items =
        [
            .. table.Columns.Reverse().Select((column, index) => new FormsItem(
                column.Name, "Text Item", null, column.Name, $"Label {index}", index == 0, true, null)),
            new FormsItem("APPROVE_BUTTON", "Push Button", null, null, "Approve", false, true, null),
        ];

        FormsModule module = new(
            "ORDERS_FORM", "Orders",
            [new FormsBlock("ORDER_BLOCK", table.Name, 10, items, [])],
            [], [], []);

        ApplicationConversion conversion = ApplicationCodeEmitter.Convert(
            schema, "ORDERS", DatabaseTarget.PostgreSql, [module]);

        GeneratedFile app = conversion.Files.Single(file => file.Path == "frontend/src/App.tsx");

        Assert.Contains("Generated from Forms block ORDER_BLOCK", app.Contents, StringComparison.Ordinal);
        Assert.Contains("Label 0", app.Contents, StringComparison.Ordinal);
        Assert.True(
            app.Contents.IndexOf("Label 0", StringComparison.Ordinal) < app.Contents.IndexOf("Label 1", StringComparison.Ordinal),
            "Columns should be rendered in the form's order.");

        // A push button is not a column and cannot be read off the API response.
        Assert.DoesNotContain("APPROVE_BUTTON", app.Contents, StringComparison.Ordinal);
        Assert.Contains(
            conversion.Findings,
            finding => finding.Construct.Contains("APPROVE_BUTTON", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_an_export_the_screen_still_falls_back_to_table_structure()
    {
        OracleSchema schema = OracleSchemaParser.Parse(OracleSamples.Schema);

        ApplicationConversion conversion = ApplicationCodeEmitter.Convert(schema, "ORDERS", DatabaseTarget.PostgreSql);
        GeneratedFile app = conversion.Files.Single(file => file.Path == "frontend/src/App.tsx");

        Assert.DoesNotContain("Generated from Forms block", app.Contents, StringComparison.Ordinal);
        Assert.Contains(
            conversion.Findings,
            finding => finding.Reason.Contains("generated from table structure", StringComparison.Ordinal));
    }

    [Fact]
    public void The_output_never_claims_to_have_read_a_forms_module()
    {
        ApplicationConversion conversion = Convert();

        Assert.Contains(
            conversion.Findings,
            finding => finding.Category == "User interface"
                       && finding.Reason.Contains("not from the original Forms modules", StringComparison.Ordinal));
    }

    [Fact]
    public void Open_endpoints_are_declared_rather_than_left_to_be_discovered() =>
        Assert.Contains(
            Convert().Findings,
            finding => finding.Category == "Authorization" && finding.Reason.Contains("unauthenticated", StringComparison.Ordinal));

    [Fact]
    public void The_readme_states_what_was_not_generated()
    {
        string readme = File(Convert(), "README.md");

        Assert.Contains("deliberately not here", readme, StringComparison.Ordinal);
        Assert.Contains("Authorization", readme, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("frontend/package.json")]
    [InlineData("frontend/tsconfig.json")]
    [InlineData("frontend/vite.config.ts")]
    [InlineData("frontend/index.html")]
    [InlineData("frontend/src/main.tsx")]
    [InlineData("frontend/src/vite-env.d.ts")]
    public void The_generated_frontend_contains_every_build_entry_point(string path)
    {
        Assert.Contains(Convert().Files, file => file.Path == path);
    }
}

public class ApplicationCodeConversionPhaseTests
{
    private const string Operator = "migration-operator@contoso.com";

    private static MigrationRunRequest Request() => new()
    {
        EngagementId = "ENG-APP",
        ApplicationName = "ORDERS",
        RequestedMode = ExecutionMode.GenerateArtifacts,
        Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
        SourceRoot = "legacy/forms",
        OutputRoot = "out/orders",
        Evidence =
        [
            Requests.Evidence("EV-INV", EvidenceKind.FormsModuleInventory),
            Requests.Evidence("EV-PLSQL", EvidenceKind.PlSqlProgramUnit),
            Requests.Evidence("EV-SCHEMA", EvidenceKind.DatabaseSchemaExport),
            Requests.Evidence("EV-TEST", EvidenceKind.TestBaseline),
        ],
        PlanApproval = Requests.Approved("plan-owner@contoso.com"),
        ExecutionApproval = HumanApproval.Pending,
    };

    private static TemporaryWorkspace SeededWorkspace()
    {
        TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/001_schema.sql", OracleSamples.Schema);
        workspace.WriteBytes("legacy/forms/ui/ACCOUNT_OPEN.fmb", [0x0A, 0x46, 0x4F, 0x52, 0x4D, 0x00, 0xFF, 0xFE]);
        return workspace;
    }

    private static Task<MigrationExecutionResult> RunAsync(TemporaryWorkspace workspace) =>
        new MigrationExecutor(workspace.Root, MigrationExecutor.DefaultAdapters()).ExecuteAsync(Request(), Operator);

    [Fact]
    public async Task The_application_tier_is_generated_without_forms_evidence()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        MigrationExecutionResult result = await RunAsync(workspace);

        PhaseOutcome outcome = result.Phases.Single(phase => phase.Phase == MigrationPhase.ApplicationCodeConversion);
        Assert.Equal(PhaseExecutionState.Executed, outcome.State);
        Assert.True(workspace.Exists("out/orders/application/backend/pom.xml"));
        Assert.True(workspace.Exists("out/orders/application/frontend/src/api.ts"));
        Assert.True(workspace.Exists("out/orders/application/CONVERSION_NOTES.md"));
    }

    [Fact]
    public async Task Unreadable_forms_modules_are_reported_in_the_notes()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        await RunAsync(workspace);

        string notes = workspace.Read("out/orders/application/CONVERSION_NOTES.md");
        Assert.Contains("were not converted", notes, StringComparison.Ordinal);
        Assert.Contains("proprietary binary", notes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generating_code_produces_no_attestation()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        MigrationExecutionResult result = await RunAsync(workspace);

        Assert.Empty(result.Attestations);
    }

    [Fact]
    public async Task A_source_tree_without_sql_fails_rather_than_inventing_an_application()
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteBytes("legacy/forms/ui/ACCOUNT_OPEN.fmb", [0x0A, 0x46, 0x4F, 0x52, 0x4D]);

        MigrationExecutionResult result = await RunAsync(workspace);

        PhaseOutcome outcome = result.Phases.Single(phase => phase.Phase == MigrationPhase.ApplicationCodeConversion);
        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.False(workspace.Exists("out/orders/application/backend/pom.xml"));
    }
}
