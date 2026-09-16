// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Fleet.Execution.Adapters;

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
        Assert.Contains("spring-boot-maven-plugin", pom, StringComparison.Ordinal);
        Assert.Contains("<goal>repackage</goal>", pom, StringComparison.Ordinal);
        Assert.Contains("azure-identity-extensions", pom, StringComparison.Ordinal);
        Assert.DoesNotContain("spring-cloud-azure-starter-jdbc-postgresql", pom, StringComparison.Ordinal);
        Assert.DoesNotContain("ojdbc", pom, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("oracle", pom, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_datasource_is_passwordless_and_carries_no_credential()
    {
        string yaml = File(Convert(), "application.yml");

        Assert.Contains("authenticationPluginClassName=com.azure.identity.extensions.jdbc.postgresql.AzurePostgresqlAuthenticationPlugin", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("password:", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixed_length_character_columns_preserve_their_postgresql_type()
    {
        string entity = File(Convert(), "BankAccount.java");

        Assert.Contains("@JdbcTypeCode(SqlTypes.CHAR)", entity, StringComparison.Ordinal);
        Assert.Contains("@Column(name = \"online_enabled\", nullable = false, columnDefinition = \"char(1)\")", entity, StringComparison.Ordinal);
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

    /// <summary>
    /// Two directories carrying one module name are two modules. One screen file is emitted, so the one it
    /// comes from is chosen by directory-qualified identity rather than by arrival order, and the other is
    /// reported: neither may be dropped, and neither may overwrite the other's output path.
    /// </summary>
    [Fact]
    public void Two_modules_of_one_name_from_separate_directories_neither_collide_nor_vanish()
    {
        OracleSchema schema = OracleSchemaParser.Parse(OracleSamples.Schema);
        OracleTable table = schema.Tables[0];

        FormsModule Module(string sourcePath) => new(
            "ORDERS", "Orders",
            [new FormsBlock("ORDER_BLOCK", table.Name, 10, [new FormsItem(table.Columns[0].Name, "Text Item", null, table.Columns[0].Name, "Account", true, true, null)], [])],
            [], [], [], sourcePath);

        // Supplied with forms-b first, so a pass cannot come from the order the modules arrived in.
        ApplicationConversion conversion = ApplicationCodeEmitter.Convert(
            schema, "ORDERS", DatabaseTarget.PostgreSql,
            [Module("legacy/forms/forms-b/ORDERS.xml"), Module("legacy/forms/forms-a/ORDERS.xml")]);

        GeneratedFile app = conversion.Files.Single(file => file.Path == "frontend/src/App.tsx");

        Assert.Contains("in module legacy/forms/forms-a/ORDERS", app.Contents, StringComparison.Ordinal);
        Assert.Contains(
            conversion.Findings,
            finding => finding.Construct == "legacy/forms/forms-b/ORDERS.ORDER_BLOCK");
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

    /// <summary>
    /// <paramref name="formsEvidence"/> declares that the run supplies Forms source. Source normalization
    /// is gated on it, so a run that carries Forms modules without it never reaches the phase that
    /// adjudicates them and the conversion is blocked rather than refused on its own terms.
    /// </summary>
    private static MigrationRunRequest Request(bool formsEvidence = false) => new()
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
            .. formsEvidence
                ? (EvidenceItem[])[Requests.Evidence("EV-SRC", EvidenceKind.FormsModuleSource)]
                : [],
        ],
        PlanApproval = Requests.Approved("plan-owner@contoso.com"),
        ExecutionApproval = HumanApproval.Pending,
    };

    /// <summary>Schema only: no Forms module of any kind, which is the path that generates CRUD screens.</summary>
    private static TemporaryWorkspace SeededWorkspace()
    {
        TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/001_schema.sql", OracleSamples.Schema);
        return workspace;
    }

    private static Task<MigrationExecutionResult> RunAsync(TemporaryWorkspace workspace, bool formsEvidence = false) =>
        new MigrationExecutor(workspace.Root, MigrationExecutor.DefaultAdapters())
            .ExecuteAsync(Request(formsEvidence), Operator);

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
    public async Task A_binary_only_forms_estate_is_refused_rather_than_answered_with_crud_screens()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();
        workspace.WriteBytes("legacy/forms/ui/ACCOUNT_OPEN.fmb", [0x0A, 0x46, 0x4F, 0x52, 0x4D, 0x00, 0xFF, 0xFE]);

        MigrationExecutionResult result = await RunAsync(workspace, formsEvidence: true);

        PhaseOutcome normalization = result.Phases.Single(phase => phase.Phase == MigrationPhase.SourceNormalization);
        Assert.Equal(PhaseExecutionState.Failed, normalization.State);
        Assert.Contains("ACCOUNT_OPEN.fmb", workspace.Read("out/orders/intermediate/source-version-report.md"), StringComparison.Ordinal);
        Assert.Contains("Forms Builder or the Forms JDAPI", normalization.Detail!, StringComparison.Ordinal);
        Assert.Contains("frmf2xml", normalization.Detail!, StringComparison.Ordinal);

        // Nothing was generated from a tree the adjudicating phase refused.
        PhaseOutcome outcome = result.Phases.Single(phase => phase.Phase == MigrationPhase.ApplicationCodeConversion);
        Assert.Equal(PhaseExecutionState.BlockedByDependency, outcome.State);
        Assert.False(workspace.Exists("out/orders/application/backend/pom.xml"));

        // The gate is application conversion only; the schema still converts from the supplied SQL.
        Assert.Equal(
            PhaseExecutionState.Executed,
            result.Phases.Single(phase => phase.Phase == MigrationPhase.DatabaseConversion).State);
    }

    /// <summary>
    /// A run that carries Forms modules but declares no Forms evidence never reaches normalization, so the
    /// conversion has to stop on the missing prerequisite rather than reading the tree a second time itself.
    /// </summary>
    [Fact]
    public async Task Forms_modules_without_the_evidence_that_reaches_normalization_block_the_conversion()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();
        workspace.WriteBytes("legacy/forms/ui/ACCOUNT_OPEN.fmb", [0x0A, 0x46, 0x4F, 0x52, 0x4D]);

        MigrationExecutionResult result = await RunAsync(workspace);

        Assert.Equal(
            PhaseExecutionState.SkippedByPlanner,
            result.Phases.Single(phase => phase.Phase == MigrationPhase.SourceNormalization).State);

        PhaseOutcome outcome = result.Phases.Single(phase => phase.Phase == MigrationPhase.ApplicationCodeConversion);
        Assert.Equal(PhaseExecutionState.BlockedByDependency, outcome.State);
        Assert.Contains("SourceNormalization", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists("out/orders/application/backend/pom.xml"));
    }

    /// <summary>
    /// One export beside several unopened modules used to normalize cleanly and generate an application
    /// that silently omitted every module nobody exported. An unrelated export is not coverage.
    /// </summary>
    [Theory]
    [InlineData(".fmb")]
    [InlineData(".mmb")]
    [InlineData(".pll")]
    [InlineData(".olb")]
    [InlineData(".fmt")]
    [InlineData(".mmt")]
    public async Task A_module_with_no_export_of_its_own_name_is_refused_even_beside_a_readable_export(string extension)
    {
        using TemporaryWorkspace workspace = SeededWorkspace();
        workspace.WriteFile("legacy/forms/ui/ACCOUNT_OPEN.xml", OracleSamples.FormsXml("12.2.1.4", "ACCOUNT_OPEN"));
        workspace.WriteBytes($"legacy/forms/ui/ACCOUNT_HISTORY{extension}", [0x0A, 0x46, 0x4F, 0x52, 0x4D]);

        MigrationExecutionResult result = await RunAsync(workspace, formsEvidence: true);

        PhaseOutcome normalization = result.Phases.Single(phase => phase.Phase == MigrationPhase.SourceNormalization);
        Assert.Equal(PhaseExecutionState.Failed, normalization.State);
        Assert.Contains($"ACCOUNT_HISTORY{extension}", normalization.Detail!, StringComparison.Ordinal);

        Assert.Equal(
            PhaseExecutionState.BlockedByDependency,
            result.Phases.Single(phase => phase.Phase == MigrationPhase.ApplicationCodeConversion).State);
        Assert.False(workspace.Exists("out/orders/application/backend/pom.xml"));
    }

    [Fact]
    public async Task Modules_whose_exports_all_carry_the_same_name_are_generated_from_the_export()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();
        workspace.WriteBytes("legacy/forms/ui/ACCOUNT_OPEN.fmb", [0x0A, 0x46, 0x4F, 0x52, 0x4D, 0x00, 0xFF, 0xFE]);
        workspace.WriteFile(
            "legacy/forms/ui/ACCOUNT_OPEN.xml",
            """
            <Module xmlns="http://xmlns.oracle.com/Forms" version="12.2.1.4" FormsVersion="12.2.1.4">
              <FormModule Name="ACCOUNT_OPEN" Title="Account opening">
                <Block Name="ACCOUNT_BLOCK" QueryDataSourceName="BANK_ACCOUNT" RecordsDisplayCount="5">
                  <Item Name="ACCOUNT_ID" ItemType="Text Item" DataType="Number" ColumnName="ACCOUNT_ID" Prompt="Account"/>
                </Block>
              </FormModule>
            </Module>
            """);

        MigrationExecutionResult result = await RunAsync(workspace, formsEvidence: true);

        Assert.Equal(
            PhaseExecutionState.Executed,
            result.Phases.Single(phase => phase.Phase == MigrationPhase.ApplicationCodeConversion).State);

        string notes = workspace.Read("out/orders/application/CONVERSION_NOTES.md");
        Assert.Contains("were read from an XML export", notes, StringComparison.Ordinal);
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
        workspace.WriteFile("legacy/forms/README.txt", "No schema was supplied with this estate.");

        MigrationExecutionResult result = await RunAsync(workspace);

        PhaseOutcome outcome = result.Phases.Single(phase => phase.Phase == MigrationPhase.ApplicationCodeConversion);
        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("'.sql'", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists("out/orders/application/backend/pom.xml"));
    }

    /// <summary>
    /// The adapter is reachable directly, so the release fields have to be checked here too rather than
    /// only at the planner boundary a caller can bypass.
    /// </summary>
    [Fact]
    public async Task An_uninterpretable_release_stops_the_adapter_before_it_writes_anything()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        MigrationRunRequest request = Request();
        PhasePlan plan = MigrationRunPlanner.Plan(request).Phases
            .Single(phase => phase.Phase == MigrationPhase.ApplicationCodeConversion);

        PhaseExecutionResult result = await new ApplicationCodeConversionAdapter().ExecuteAsync(
            new PhaseExecutionContext(
                workspace.Root,
                request.SourceRoot,
                request.OutputRoot,
                plan,
                request with { OracleFormsVersion = "banana" },
                (_, _) => { }),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("were not accepted", result.FailureReason!, StringComparison.Ordinal);
        Assert.False(workspace.Exists("out/orders/application/backend/pom.xml"));
    }
}
