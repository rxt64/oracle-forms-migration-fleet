// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Fleet.Execution.Adapters;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// The normalization phase is the gate that decides whether any Forms source was readable at all. These
/// tests hold it to refusing binary-only estates at every release rather than letting a later phase
/// generate screens from table structure and present them as a migration of modules nobody opened.
/// </summary>
public class SourceNormalizationAdapterTests
{
    private const string Operator = "migration-operator@contoso.com";
    private const string ReportPath = "out/orders/intermediate/source-version-report.md";
    private const string ManifestPath = "out/orders/intermediate/forms-normalization-manifest.json";
    private const string IrPath = "out/orders/intermediate/forms-ir.json";

    private static MigrationRunRequest Request(string formsVersion = "unknown", string databaseVersion = "unknown") => new()
    {
        EngagementId = "ENG-NORM",
        ApplicationName = "ORDERS",
        RequestedMode = ExecutionMode.GenerateArtifacts,
        Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
        OracleFormsVersion = formsVersion,
        OracleDatabaseVersion = databaseVersion,
        SourceRoot = "legacy/forms",
        OutputRoot = "out/orders",
        Evidence =
        [
            Requests.Evidence("EV-INV", EvidenceKind.FormsModuleInventory),
            Requests.Evidence("EV-SRC", EvidenceKind.FormsModuleSource),
            Requests.Evidence("EV-PLSQL", EvidenceKind.PlSqlProgramUnit),
            Requests.Evidence("EV-SCHEMA", EvidenceKind.DatabaseSchemaExport),
            Requests.Evidence("EV-TEST", EvidenceKind.TestBaseline),
        ],
        PlanApproval = Requests.Approved("plan-owner@contoso.com"),
    };

    /// <summary>A Forms export in the shape frmf2xml produces, declaring whichever release the test needs.</summary>
    private static string FormsXml(
        string? version,
        string moduleName = "ORDER_ENTRY",
        string triggerBody = "BEGIN NULL; END;") =>
        $"""
         <?xml version="1.0" encoding="UTF-8"?>
         <Module xmlns="http://xmlns.oracle.com/Forms"{(version is null ? string.Empty : $" version=\"{version}\" FormsVersion=\"{version}\"")}>
           <FormModule Name="{moduleName}" Title="Order entry">
             <Trigger Name="WHEN-NEW-FORM-INSTANCE" TriggerText="{triggerBody}"/>
             <Block Name="ORDER_BLOCK" QueryDataSourceName="BANK_ACCOUNT" RecordsDisplayCount="10">
               <Item Name="ACCOUNT_ID" ItemType="Text Item" DataType="Number" ColumnName="ACCOUNT_ID" Prompt="Account" Required="true"/>
             </Block>
           </FormModule>
         </Module>
         """;

    private static TemporaryWorkspace Workspace(Action<TemporaryWorkspace> seed)
    {
        TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/001_schema.sql", OracleSamples.Schema);
        seed(workspace);
        return workspace;
    }

    private static async Task<PhaseOutcome> NormalizeAsync(TemporaryWorkspace workspace, MigrationRunRequest request)
    {
        MigrationExecutionResult result = await new MigrationExecutor(workspace.Root, [new SourceNormalizationAdapter()])
            .ExecuteAsync(request, Operator);

        return result.Phases.Single(phase => phase.Phase == MigrationPhase.SourceNormalization);
    }

    /// <summary>
    /// Runs the adapter without the planner's evidence gate, for the database-only case where no Forms
    /// evidence is declared at all and the planner therefore never authorizes the phase.
    /// </summary>
    private static Task<PhaseExecutionResult> NormalizeDirectAsync(TemporaryWorkspace workspace, MigrationRunRequest request)
    {
        PhasePlan plan = MigrationRunPlanner.Plan(request).Phases.Single(phase => phase.Phase == MigrationPhase.SourceNormalization);

        return new SourceNormalizationAdapter().ExecuteAsync(
            new PhaseExecutionContext(workspace.Root, request.SourceRoot, request.OutputRoot, plan, request, (_, _) => { }),
            CancellationToken.None);
    }

    [Fact]
    public async Task A_binary_only_6i_estate_fails_closed_and_keeps_its_diagnostics()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
        {
            space.WriteBytes("legacy/forms/ui/ORDER_ENTRY.fmb", [0x0A, 0x46, 0x4F, 0x52, 0x4D, 0x00, 0xFF, 0xFE]);
            space.WriteBytes("legacy/forms/ui/ORDER_MENU.mmb", [0x0A, 0x4D, 0x45, 0x4E, 0x55]);
            space.WriteBytes("legacy/forms/lib/SHARED.pll", [0x0A, 0x50, 0x4C, 0x4C]);
        });

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("6i"));

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("proprietary binary", outcome.Detail!, StringComparison.Ordinal);
        Assert.Contains("frmf2xml", outcome.Detail!, StringComparison.Ordinal);
        Assert.Contains(".olb, .pll, .mmb, .fmb", outcome.Detail!, StringComparison.Ordinal);

        // The diagnostics are the point of the refusal, so they survive it and stay attributed.
        Assert.True(workspace.Exists(ReportPath));
        Assert.True(workspace.Exists(ManifestPath));
        Assert.Contains(outcome.Artifacts, artifact => artifact.Path == ReportPath);
        Assert.Contains(outcome.Artifacts, artifact => artifact.Path == ManifestPath);

        // No intermediate representation exists, so none is written and nothing downstream can cite one.
        Assert.False(workspace.Exists(IrPath));

        string report = workspace.Read(ReportPath);
        Assert.Contains("10.1.2", report, StringComparison.Ordinal);
        Assert.Contains("FRM-18130", report, StringComparison.Ordinal);
        Assert.Contains("ORDER_ENTRY.fmb", report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("9i")]
    [InlineData("10g")]
    [InlineData("11g")]
    [InlineData("12c")]
    public async Task A_binary_only_estate_fails_at_every_release_not_only_6i(string formsVersion)
    {
        using TemporaryWorkspace workspace = Workspace(space =>
            space.WriteBytes("legacy/forms/ui/ORDER_ENTRY.fmb", [0x0A, 0x46, 0x4F, 0x52, 0x4D]));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request(formsVersion));

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task A_6i_export_normalizes_and_records_the_bridge_recommendation()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
            space.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", FormsXml("6.0.8.28")));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("6i"));

        Assert.Equal(PhaseExecutionState.Executed, outcome.State);
        Assert.True(workspace.Exists(IrPath));
        Assert.Contains(outcome.Findings, finding => finding.Contains("FRM-18130", StringComparison.Ordinal));
        Assert.Contains(outcome.Findings, finding => finding.Contains("10.1.2", StringComparison.Ordinal));

        using JsonDocument ir = JsonDocument.Parse(workspace.Read(IrPath));
        Assert.Equal("6i", ir.RootElement.GetProperty("formsFamily").GetString());
        Assert.Equal("ORDER_ENTRY", ir.RootElement.GetProperty("modules")[0].GetProperty("name").GetString());
        Assert.Equal("ORDER_BLOCK", ir.RootElement.GetProperty("modules")[0].GetProperty("blocks")[0].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("6i", "6.0.8.28", "6i")]
    [InlineData("9i", "9.0.2.0", "9i")]
    [InlineData("10g", "10.1.2.3", "10g")]
    [InlineData("11g", "11.1.2.2", "11g")]
    [InlineData("12c", "12.2.1.4", "12c")]
    public async Task A_text_export_from_each_intake_family_reaches_strict_ir_and_application_generation(
        string requestedVersion,
        string exportVersion,
        string expectedFamily)
    {
        using TemporaryWorkspace workspace = Workspace(space =>
            space.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", FormsXml(exportVersion)));

        MigrationExecutionResult result = await new MigrationExecutor(
                workspace.Root,
                [new SourceNormalizationAdapter(), new ApplicationCodeConversionAdapter()])
            .ExecuteAsync(Request(requestedVersion), Operator);

        Assert.Equal(
            PhaseExecutionState.Executed,
            result.Phases.Single(phase => phase.Phase == MigrationPhase.SourceNormalization).State);
        Assert.Equal(
            PhaseExecutionState.Executed,
            result.Phases.Single(phase => phase.Phase == MigrationPhase.ApplicationCodeConversion).State);

        using JsonDocument ir = JsonDocument.Parse(workspace.Read(IrPath));
        Assert.Equal("oracle-forms-migration-fleet/source-normalization", ir.RootElement.GetProperty("generator").GetString());
        Assert.Equal("2", ir.RootElement.GetProperty("schemaVersion").GetString());
        Assert.True(ir.RootElement.GetProperty("normalized").GetBoolean());
        Assert.Equal("declared by the export and matching the run", ir.RootElement.GetProperty("versionAuthority").GetString());
        Assert.Equal(expectedFamily, ir.RootElement.GetProperty("formsFamily").GetString());
        Assert.Equal("legacy/forms", ir.RootElement.GetProperty("sourceRoot").GetString());
        Assert.Equal(exportVersion, ir.RootElement.GetProperty("modules")[0].GetProperty("declaredVersion").GetString());
        Assert.Equal(expectedFamily, ir.RootElement.GetProperty("modules")[0].GetProperty("declaredFamily").GetString());
        Assert.Equal("legacy/forms/ui/ORDER_ENTRY.xml", ir.RootElement.GetProperty("modules")[0].GetProperty("sourcePath").GetString());

        Assert.Contains("<artifactId>migrated-backend</artifactId>", workspace.Read("out/orders/application/backend/pom.xml"), StringComparison.Ordinal);
        Assert.Contains("\"build\": \"tsc --noEmit && vite build\"", workspace.Read("out/orders/application/frontend/package.json"), StringComparison.Ordinal);
        Assert.Contains(
            "were read from an XML export",
            workspace.Read("out/orders/application/CONVERSION_NOTES.md"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changed_trigger_body_changes_the_versioned_intermediate_representation()
    {
        using TemporaryWorkspace original = Workspace(space =>
            space.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", FormsXml("12.2.1.4")));
        using TemporaryWorkspace changed = Workspace(space =>
            space.WriteFile(
                "legacy/forms/ui/ORDER_ENTRY.xml",
                FormsXml("12.2.1.4", triggerBody: "BEGIN EXECUTE_QUERY; END;")));

        Assert.Equal(PhaseExecutionState.Executed, (await NormalizeAsync(original, Request("12c"))).State);
        Assert.Equal(PhaseExecutionState.Executed, (await NormalizeAsync(changed, Request("12c"))).State);

        string originalIr = original.Read(IrPath);
        string changedIr = changed.Read(IrPath);

        Assert.NotEqual(originalIr, changedIr);

        using JsonDocument document = JsonDocument.Parse(changedIr);
        JsonElement trigger = document.RootElement.GetProperty("modules")[0].GetProperty("triggers")[0];
        Assert.Equal("WHEN-NEW-FORM-INSTANCE", trigger.GetProperty("name").GetString());
        Assert.Equal("ORDER_ENTRY", trigger.GetProperty("scope").GetString());
        Assert.Equal("BEGIN EXECUTE_QUERY; END;", trigger.GetProperty("body").GetString());
        Assert.Equal("Attribute", trigger.GetProperty("bodyEncoding").GetString());
    }

    [Fact]
    public async Task Oversized_source_trigger_body_fails_during_normalization_and_writes_no_ir()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
            space.WriteFile(
                "legacy/forms/ui/ORDER_ENTRY.xml",
                FormsXml("12.2.1.4", triggerBody: new string('X', FormsXmlDocument.MaxTriggerBodyCharacters + 1))));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("12c"));

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("refused rather than writing an IR", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task Duplicate_source_trigger_identity_fails_during_normalization_and_writes_no_ir()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
            space.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", """
                <FormModule xmlns="http://xmlns.oracle.com/Forms" Name="ORDER_ENTRY" FormsVersion="12.2.1.4">
                  <Trigger Name="WHEN-NEW-FORM-INSTANCE" TriggerText="BEGIN FIRST; END;"/>
                  <Trigger Name="WHEN-NEW-FORM-INSTANCE" TriggerText="BEGIN SECOND; END;"/>
                </FormModule>
                """));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("12c"));

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("more than one trigger", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

        [Fact]
        public async Task Duplicate_source_item_identity_fails_during_normalization_and_writes_no_ir()
        {
                using TemporaryWorkspace workspace = Workspace(space =>
                        space.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", """
                                <FormModule xmlns="http://xmlns.oracle.com/Forms" Name="ORDER_ENTRY" FormsVersion="12.2.1.4">
                                    <Block Name="ORDER_BLOCK">
                                        <Item Name="DUPLICATE"/>
                                        <Item Name="DUPLICATE"/>
                                    </Block>
                                </FormModule>
                                """));

                PhaseOutcome outcome = await NormalizeAsync(workspace, Request("12c"));

                Assert.Equal(PhaseExecutionState.Failed, outcome.State);
                Assert.Contains("more than one item", outcome.Detail!, StringComparison.Ordinal);
                Assert.False(workspace.Exists(IrPath));
        }

    [Fact]
    public async Task A_declared_release_that_contradicts_the_run_fails_closed()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
            space.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", FormsXml("12.2.1.4")));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("6i"));

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("6i", outcome.Detail!, StringComparison.Ordinal);
        Assert.Contains("12c", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task Module_count_across_multiple_exports_beyond_the_reader_limit_writes_no_ir()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
        {
            for (int index = 0; index <= FormsIntermediateReader.MaxModules; index++)
            {
                space.WriteFile(
                    $"legacy/forms/ui/M{index}.xml",
                    FormsXml("12.2.1.4", moduleName: $"M{index}"));
            }
        });

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("12c"));

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("5001 Forms modules across the estate", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task Text_modules_without_a_normalized_export_fail_with_the_two_step_route()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
        {
            space.WriteFile("legacy/forms/ui/ORDER_ENTRY.fmt", "FORM MODULE ORDER_ENTRY\n");
            space.WriteFile("legacy/forms/ui/ORDER_MENU.mmt", "MENU MODULE ORDER_MENU\n");
        });

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("6i"));

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("two steps", outcome.Detail!, StringComparison.Ordinal);
        Assert.Contains("6i Builder or Compiler", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task The_demonstrated_12_2_1_4_export_normalizes_when_the_run_declares_no_release()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
            space.WriteFile(
                "legacy/forms/ui/005_bank_account_request_form.xml",
                File.ReadAllText(Path.Combine(
                    RepositoryRoot(), "infra", "forms-demo", "estate", "005_bank_account_request_form.xml"))));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request(databaseVersion: "23ai"));

        Assert.Equal(PhaseExecutionState.Executed, outcome.State);
        Assert.True(workspace.Exists(IrPath));

        using JsonDocument ir = JsonDocument.Parse(workspace.Read(IrPath));
        Assert.Equal("12c", ir.RootElement.GetProperty("formsFamily").GetString());
        Assert.Equal("BANK_ACCOUNT_REQUEST_FORM", ir.RootElement.GetProperty("modules")[0].GetProperty("name").GetString());

        using JsonDocument manifest = JsonDocument.Parse(workspace.Read(ManifestPath));
        Assert.Equal("12.2.1.4", manifest.RootElement.GetProperty("detectedForms").GetProperty("requested").GetString());
        Assert.Equal("23ai", manifest.RootElement.GetProperty("requestedDatabase").GetProperty("family").GetString());
    }

    [Fact]
    public async Task An_export_without_a_version_is_accepted_only_as_an_unverified_operator_claim()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
            space.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", FormsXml(null)));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("11g"));

        Assert.Equal(PhaseExecutionState.Executed, outcome.State);
        Assert.Contains(outcome.Findings, finding =>
            finding.Contains("supplied by the operator", StringComparison.Ordinal)
            && finding.Contains("did not verify it", StringComparison.Ordinal));

        using JsonDocument manifest = JsonDocument.Parse(workspace.Read(ManifestPath));
        Assert.Equal("operator-supplied, unverified by the export", manifest.RootElement.GetProperty("versionAuthority").GetString());

        // The export declared nothing, so nothing may read back as though it had.
        Assert.False(manifest.RootElement.TryGetProperty("detectedForms", out _));
        Assert.Equal("11g", manifest.RootElement.GetProperty("effectiveForms").GetProperty("family").GetString());
        Assert.Contains("not declared by any supplied export", workspace.Read(ReportPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_export_without_a_version_and_no_declared_release_fails()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
            space.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", FormsXml(null)));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request());

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("not established from either side", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public void An_uninterpretable_declared_release_is_refused_at_intake_before_any_phase_runs()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Request("banana"));

        Assert.Empty(plan.Phases);
        Assert.Contains(plan.Blockers, blocker => blocker.Contains("OracleFormsVersion", StringComparison.Ordinal));
        Assert.Contains(plan.Blockers, blocker => blocker.Contains("was not interpreted", StringComparison.Ordinal)
            || blocker.Contains("matches no Oracle Forms release", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_uninterpretable_declared_release_still_stops_the_adapter_if_it_is_reached_directly()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
            space.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", FormsXml("12.2.1.4")));

        MigrationRunRequest request = Request("12c");
        PhasePlan plan = MigrationRunPlanner.Plan(request).Phases.Single(phase => phase.Phase == MigrationPhase.SourceNormalization);

        PhaseExecutionResult result = await new SourceNormalizationAdapter().ExecuteAsync(
            new PhaseExecutionContext(
                workspace.Root, request.SourceRoot, request.OutputRoot, plan, request with { OracleFormsVersion = "banana" }, (_, _) => { }),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("was not interpreted", result.FailureReason!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task An_estate_with_no_forms_source_and_no_forms_evidence_normalizes_nothing_and_says_so()
    {
        // A database-only run is legitimate. The gap is recorded so no later artifact can imply a Forms migration.
        using TemporaryWorkspace workspace = Workspace(_ => { });

        MigrationRunRequest request = Request("6i") with
        {
            Evidence =
            [
                Requests.Evidence("EV-PLSQL", EvidenceKind.PlSqlProgramUnit),
                Requests.Evidence("EV-SCHEMA", EvidenceKind.DatabaseSchemaExport),
                Requests.Evidence("EV-TEST", EvidenceKind.TestBaseline),
            ],
        };

        PhaseExecutionResult result = await NormalizeDirectAsync(workspace, request);

        Assert.True(result.Succeeded);
        Assert.Contains(result.Findings, finding =>
            finding.Contains("No Oracle Forms module source of any kind", StringComparison.Ordinal));
        Assert.True(workspace.Exists(ReportPath));

        // The representation exists and is empty, so nothing downstream can read a module out of it.
        using JsonDocument ir = JsonDocument.Parse(workspace.Read(IrPath));
        Assert.Empty(ir.RootElement.GetProperty("modules").EnumerateArray());
    }

    [Fact]
    public async Task Declared_forms_evidence_with_no_forms_source_on_disk_fails_closed()
    {
        // The request attests that Forms source exists. Succeeding here would let a schema-only generation
        // inherit an attestation about modules that were never supplied.
        using TemporaryWorkspace workspace = Workspace(_ => { });

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("6i"));

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("contradict each other", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    /// <summary>
    /// One export beside modules nobody opened used to normalize with a warning, and the run then generated
    /// an application that silently omitted every unexported module.
    /// </summary>
    [Theory]
    [InlineData(".fmb")]
    [InlineData(".mmb")]
    [InlineData(".pll")]
    [InlineData(".olb")]
    [InlineData(".fmt")]
    [InlineData(".mmt")]
    public async Task A_module_with_no_export_of_its_own_name_fails_the_estate(string extension)
    {
        using TemporaryWorkspace workspace = Workspace(space =>
        {
            space.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", FormsXml("12.2.1.4"));
            space.WriteBytes($"legacy/forms/ui/ORDER_HISTORY{extension}", [0x0A, 0x46, 0x4F, 0x52, 0x4D]);
        });

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("12c"));

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains($"ORDER_HISTORY{extension}", outcome.Detail!, StringComparison.Ordinal);
        Assert.Contains("partially normalized", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));

        // The diagnostics survive the refusal and name the file that was never opened.
        using JsonDocument manifest = JsonDocument.Parse(workspace.Read(ManifestPath));
        Assert.Contains(
            manifest.RootElement.GetProperty("files").EnumerateArray(),
            file => file.GetProperty("path").GetString()!.EndsWith($"ORDER_HISTORY{extension}", StringComparison.Ordinal)
                    && !file.GetProperty("readAsText").GetBoolean());
    }

    [Fact]
    public async Task A_module_covered_by_an_export_of_the_same_name_normalizes()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
        {
            space.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", FormsXml("12.2.1.4"));
            space.WriteBytes("legacy/forms/ui/ORDER_ENTRY.fmb", [0x0A, 0x46, 0x4F, 0x52, 0x4D]);
        });

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("12c"));

        Assert.Equal(PhaseExecutionState.Executed, outcome.State);
        Assert.True(workspace.Exists(IrPath));
    }

    /// <summary>
    /// Coverage used to be decided on the file name alone, so an export sitting in one directory stood in
    /// for a module of the same name in another that nobody opened. Which module a bare name resolves to
    /// is a FORMS_PATH question, so the match has to be local to the directory the module came from.
    /// </summary>
    [Fact]
    public async Task An_export_in_another_directory_is_not_coverage_for_a_module_beside_it()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
        {
            space.WriteBytes("legacy/forms/forms-a/ORDERS.fmb", [0x0A, 0x46, 0x4F, 0x52, 0x4D]);
            space.WriteFile("legacy/forms/forms-b/ORDERS.xml", FormsXml("12.2.1.4", "ORDERS"));
        });

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("12c"));

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("legacy/forms/forms-a/ORDERS.fmb", outcome.Detail!, StringComparison.Ordinal);
        Assert.Contains("in its own directory `legacy/forms/forms-a`", outcome.Detail!, StringComparison.Ordinal);
        Assert.Contains("FORMS_PATH", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    /// <summary>
    /// Two directories carrying one module name are two modules, not a clash. Refusing them globally would
    /// reject an estate that supplied a readable export for both.
    /// </summary>
    [Fact]
    public async Task Two_directories_that_each_supply_their_own_export_of_one_module_name_normalize()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
        {
            space.WriteBytes("legacy/forms/forms-a/ORDERS.fmb", [0x0A, 0x46, 0x4F, 0x52, 0x4D]);
            space.WriteFile("legacy/forms/forms-a/ORDERS.xml", FormsXml("12.2.1.4", "ORDERS"));
            space.WriteBytes("legacy/forms/forms-b/ORDERS.fmb", [0x0A, 0x46, 0x4F, 0x52, 0x4D]);
            space.WriteFile("legacy/forms/forms-b/ORDERS.xml", FormsXml("12.2.1.4", "ORDERS"));
        });

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("12c"));

        Assert.Equal(PhaseExecutionState.Executed, outcome.State);
        Assert.True(workspace.Exists(IrPath));

        using JsonDocument ir = JsonDocument.Parse(workspace.Read(IrPath));
        string[] sources =
        [
            .. ir.RootElement.GetProperty("modules").EnumerateArray()
                .Select(module => module.GetProperty("sourcePath").GetString()!),
        ];

        Assert.Equal(2, sources.Length);
        Assert.Contains("legacy/forms/forms-a/ORDERS.xml", sources);
        Assert.Contains("legacy/forms/forms-b/ORDERS.xml", sources);
    }

    /// <summary>
    /// The whole way through: normalization accepts two directories that each supply their own export of
    /// one module name, and the conversion that reads what it wrote accepts both modules rather than
    /// refusing the pair as a duplicate name or dropping one of them into a shared output path.
    /// </summary>
    [Fact]
    public async Task Two_directories_that_share_a_module_name_survive_normalization_and_conversion()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
        {
            space.WriteBytes("legacy/forms/forms-a/ORDERS.fmb", [0x0A, 0x46, 0x4F, 0x52, 0x4D]);
            space.WriteFile("legacy/forms/forms-a/ORDERS.xml", FormsXml("12.2.1.4", "ORDERS"));
            space.WriteBytes("legacy/forms/forms-b/ORDERS.fmb", [0x0A, 0x46, 0x4F, 0x52, 0x4D]);
            space.WriteFile("legacy/forms/forms-b/ORDERS.xml", FormsXml("12.2.1.4", "ORDERS"));
        });

        MigrationRunRequest request = Request("12c");

        Assert.Equal(PhaseExecutionState.Executed, (await NormalizeAsync(workspace, request)).State);

        FormsIntermediateRead read = FormsIntermediateReader.Read(workspace.Read(IrPath), request.SourceRoot);

        Assert.Null(read.Error);
        Assert.Equal(
            ["legacy/forms/forms-a/ORDERS", "legacy/forms/forms-b/ORDERS"],
            read.Modules!.Select(module => module.QualifiedName).Order(StringComparer.Ordinal));

        PhaseExecutionResult conversion = await ConvertAsync(workspace, request);

        Assert.True(conversion.Succeeded, conversion.FailureReason);

        // One screen is emitted, chosen by qualified identity, and the other module is reported rather
        // than dropped. Neither may vanish, and neither may overwrite the other's output.
        string app = workspace.Read("out/orders/application/frontend/src/App.tsx");

        Assert.Contains("in module legacy/forms/forms-a/ORDERS", app, StringComparison.Ordinal);
        Assert.Contains(conversion.Findings, finding => finding.Contains("legacy/forms/forms-b/ORDERS.ORDER_BLOCK", StringComparison.Ordinal));
        Assert.DoesNotContain(conversion.Findings, finding => finding.Contains("more than one module", StringComparison.Ordinal));
    }

    private static Task<PhaseExecutionResult> ConvertAsync(TemporaryWorkspace workspace, MigrationRunRequest request)
    {
        PhasePlan plan = MigrationRunPlanner.Plan(request).Phases
            .Single(phase => phase.Phase == MigrationPhase.ApplicationCodeConversion);

        return new ApplicationCodeConversionAdapter().ExecuteAsync(
            new PhaseExecutionContext(workspace.Root, request.SourceRoot, request.OutputRoot, plan, request, (_, _) => { })
            {
                CompletedPhases =
                [
                    new PhaseOutcome(MigrationPhase.SourceNormalization, PhaseStatus.Planned, PhaseExecutionState.Executed, [], [], null),
                ],
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Directory scoping loosens coverage across directories, so it must not loosen it inside one: two
    /// copies of a module in one directory are still ambiguous.
    /// </summary>
    [Fact]
    public async Task Two_copies_of_one_module_in_one_directory_stay_ambiguous()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
        {
            space.WriteFile("legacy/forms/ui/ORDERS.xml", FormsXml("12.2.1.4", "ORDERS"));
            space.WriteBytes("legacy/forms/ui/ORDERS.fmb", [0x0A, 0x46, 0x4F, 0x52, 0x4D]);
            space.WriteBytes("legacy/forms/ui/001_ORDERS.fmb", [0x0A, 0x46, 0x4F, 0x52, 0x4D]);
        });

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("12c"));

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("supplied twice from one directory", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task Two_exports_of_one_module_in_one_directory_stay_ambiguous()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
        {
            space.WriteFile("legacy/forms/ui/ORDERS.xml", FormsXml("12.2.1.4", "ORDERS"));
            space.WriteFile("legacy/forms/ui/001_ORDERS.xml", FormsXml("12.2.1.4", "ORDERS"));
        });

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("12c"));

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("both export module 'ORDERS' from the same directory", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    /// <summary>
    /// A FormModule export beside a menu module of the same name is still not coverage for it, no matter
    /// how local the match is: this parser has no representation for a menu at all.
    /// </summary>
    [Theory]
    [InlineData(".mmb")]
    [InlineData(".pll")]
    [InlineData(".olb")]
    public async Task A_local_export_is_still_not_coverage_for_another_module_type(string extension)
    {
        using TemporaryWorkspace workspace = Workspace(space =>
        {
            space.WriteFile("legacy/forms/ui/ORDERS.xml", FormsXml("12.2.1.4", "ORDERS"));
            space.WriteBytes($"legacy/forms/ui/ORDERS{extension}", [0x0A, 0x46, 0x4F, 0x52, 0x4D]);
        });

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("12c"));

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("has no representation for this module type at all", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task The_manifest_records_the_real_size_of_every_supplied_xml_file()
    {
        string export = FormsXml("12.2.1.4");
        using TemporaryWorkspace workspace = Workspace(space => space.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", export));

        await NormalizeAsync(workspace, Request("12c"));

        using JsonDocument manifest = JsonDocument.Parse(workspace.Read(ManifestPath));
        JsonElement file = manifest.RootElement.GetProperty("files").EnumerateArray()
            .Single(entry => entry.GetProperty("path").GetString()!.EndsWith("ORDER_ENTRY.xml", StringComparison.Ordinal));

        Assert.Equal(new FileInfo(workspace.Absolute("legacy/forms/ui/ORDER_ENTRY.xml")).Length, file.GetProperty("bytes").GetInt64());
        Assert.True(file.GetProperty("bytes").GetInt64() > 0);
    }

    [Fact]
    public async Task Xml_that_does_not_parse_fails_rather_than_reading_as_an_absent_estate()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
            space.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", "<Module><FormModule Name=\"ORDER\"></Module>"));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("12c"));

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("could not be parsed as XML", outcome.Detail!, StringComparison.Ordinal);
        Assert.Contains("ORDER_ENTRY.xml", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
        Assert.True(workspace.Exists(ManifestPath));
    }

    [Fact]
    public async Task An_export_carrying_a_dtd_is_refused_rather_than_resolved()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
            space.WriteFile(
                "legacy/forms/ui/ORDER_ENTRY.xml",
                """
                <?xml version="1.0"?>
                <!DOCTYPE Module [ <!ENTITY payload SYSTEM "file:///c:/windows/win.ini"> ]>
                <Module><FormModule Name="ORDER_ENTRY">&payload;</FormModule></Module>
                """));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("12c"));

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("could not be parsed as XML", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task Xml_that_carries_no_form_module_is_not_treated_as_a_forms_export()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
            space.WriteFile("legacy/forms/ui/layout.xml", "<layout><panel name=\"orders\"/></layout>"));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("12c"));

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("none carries a FormModule element", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    /// <summary>
    /// A valid root with foreign structure grafted underneath it is the case a root-only namespace check
    /// misses. Reading it would have added a block, item, trigger, or program unit that Oracle never
    /// exported to the intermediate representation the application converter generates screens from.
    /// </summary>
    [Theory]
    [InlineData("<evil:Block xmlns:evil=\"urn:example:evil\" Name=\"INJECTED\" QueryDataSourceName=\"BANK_STAFF_USER\"/>")]
    [InlineData("<evil:Item xmlns:evil=\"urn:example:evil\" Name=\"INJECTED\" ColumnName=\"PASSWORD_HASH\"/>")]
    [InlineData("<evil:Trigger xmlns:evil=\"urn:example:evil\" Name=\"WHEN-INJECTED\"/>")]
    [InlineData("<evil:ProgramUnit xmlns:evil=\"urn:example:evil\" Name=\"INJECTED\"/>")]
    [InlineData("<evil:LOV xmlns:evil=\"urn:example:evil\" Name=\"INJECTED\"/>")]
    public async Task A_nested_foreign_namespace_export_is_refused_and_normalizes_nothing(string injected)
    {
        using TemporaryWorkspace workspace = Workspace(space => space.WriteFile(
            "legacy/forms/ui/ORDER_ENTRY.xml",
            $"""
             <?xml version="1.0" encoding="UTF-8"?>
             <Module xmlns="http://xmlns.oracle.com/Forms" version="12.2.1.4">
               <FormModule Name="ORDER_ENTRY" Title="Order entry">
                 <Block Name="ORDER_BLOCK" QueryDataSourceName="BANK_ACCOUNT">
                   <Item Name="ACCOUNT_ID" ItemType="Text Item" ColumnName="ACCOUNT_ID"/>
                   {injected}
                 </Block>
               </FormModule>
             </Module>
             """));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("12c"));

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("ORDER_ENTRY.xml", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task A_namespaced_attribute_on_a_forms_element_is_refused_and_normalizes_nothing()
    {
        using TemporaryWorkspace workspace = Workspace(space => space.WriteFile(
            "legacy/forms/ui/ORDER_ENTRY.xml",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <Module xmlns="http://xmlns.oracle.com/Forms" version="12.2.1.4">
              <FormModule Name="ORDER_ENTRY">
                <Block xmlns:evil="urn:example:evil" evil:Name="INJECTED" Name="ORDER_BLOCK" QueryDataSourceName="BANK_ACCOUNT"/>
              </FormModule>
            </Module>
            """));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("12c"));

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("namespace-qualified attribute", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task A_form_module_with_no_block_carries_no_structure_to_normalize()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
            space.WriteFile(
                "legacy/forms/ui/ORDER_ENTRY.xml",
                """
                <Module xmlns="http://xmlns.oracle.com/Forms" version="12.2.1.4">
                  <FormModule Name="ORDER_ENTRY" Title="Order entry"/>
                </Module>
                """));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("12c"));

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("no block in any of them", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task Two_exports_declaring_different_patch_levels_of_one_family_conflict()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
        {
            space.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", FormsXml("12.2.1.3"));
            space.WriteFile("legacy/forms/ui/ORDER_HISTORY.xml", FormsXml("12.2.1.4", "ORDER_HISTORY"));
        });

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("12c"));

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("two Oracle Forms 12c releases that are not the same release", outcome.Detail!, StringComparison.Ordinal);
        Assert.Contains("ORDER_ENTRY.xml", outcome.Detail!, StringComparison.Ordinal);
        Assert.Contains("ORDER_HISTORY.xml", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task Two_exports_declaring_different_families_conflict()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
        {
            space.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", FormsXml("12.2.1.4"));
            space.WriteFile("legacy/forms/ui/ORDER_HISTORY.xml", FormsXml("11.1.2", "ORDER_HISTORY"));
        });

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request());

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("more than one Oracle Forms family", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task One_file_declaring_two_different_exact_releases_conflicts()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
            space.WriteFile(
                "legacy/forms/ui/ORDER_ENTRY.xml",
                """
                <Module xmlns="http://xmlns.oracle.com/Forms" version="12.2.1.3" FormsVersion="12.2.1.4">
                  <FormModule Name="ORDER_ENTRY" Title="Order entry">
                    <Block Name="ORDER_BLOCK" QueryDataSourceName="BANK_ACCOUNT">
                      <Item Name="ACCOUNT_ID" ItemType="Text Item" ColumnName="ACCOUNT_ID"/>
                    </Block>
                  </FormModule>
                </Module>
                """));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("12c"));

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("two Oracle Forms 12c releases that are not the same release", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    /// <summary>
    /// A wildcard patch family and the exact release inside it are one claim at two precisions. Reading
    /// them as a contradiction would refuse an estate whose exports agree.
    /// </summary>
    [Fact]
    public async Task A_wildcard_patch_family_beside_the_exact_release_inside_it_is_not_a_conflict()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
            space.WriteFile(
                "legacy/forms/ui/ORDER_ENTRY.xml",
                """
                <Module xmlns="http://xmlns.oracle.com/Forms" version="12.2.1.x" FormsVersion="12.2.1.4">
                  <FormModule Name="ORDER_ENTRY" Title="Order entry">
                    <Block Name="ORDER_BLOCK" QueryDataSourceName="BANK_ACCOUNT">
                      <Item Name="ACCOUNT_ID" ItemType="Text Item" ColumnName="ACCOUNT_ID"/>
                    </Block>
                  </FormModule>
                </Module>
                """));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("12c"));

        Assert.Equal(PhaseExecutionState.Executed, outcome.State);

        using JsonDocument manifest = JsonDocument.Parse(workspace.Read(ManifestPath));
        Assert.Equal("12.2.1.4", manifest.RootElement.GetProperty("detectedForms").GetProperty("release").GetString());
    }

    /// <summary>A broad intake value is not a contradiction of a precise export inside the same lineage.</summary>
    [Fact]
    public async Task A_broad_intake_release_matches_a_precise_export_of_the_same_lineage()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
            space.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", FormsXml("12.2.1.4")));

        Assert.Equal(PhaseExecutionState.Executed, (await NormalizeAsync(workspace, Request("12.2"))).State);
    }

    [Fact]
    public async Task A_precise_intake_release_that_diverges_from_the_export_fails_closed()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
            space.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", FormsXml("12.2.1.4")));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("12.2.1.3"));

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("contradicts the run", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task A_family_alias_beside_the_precise_release_it_names_is_not_a_conflict()
    {
        using TemporaryWorkspace workspace = Workspace(space =>
            space.WriteFile(
                "legacy/forms/ui/ORDER_ENTRY.xml",
                """
                <Module xmlns="http://xmlns.oracle.com/Forms" version="12c" FormsVersion="12.2.1.4">
                  <FormModule Name="ORDER_ENTRY" Title="Order entry">
                    <Block Name="ORDER_BLOCK" QueryDataSourceName="BANK_ACCOUNT">
                      <Item Name="ACCOUNT_ID" ItemType="Text Item" ColumnName="ACCOUNT_ID"/>
                    </Block>
                  </FormModule>
                </Module>
                """));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("12c"));

        Assert.Equal(PhaseExecutionState.Executed, outcome.State);

        using JsonDocument manifest = JsonDocument.Parse(workspace.Read(ManifestPath));
        Assert.Equal("12.2.1.4", manifest.RootElement.GetProperty("detectedForms").GetProperty("release").GetString());
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "oracle-forms-migration-fleet.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The repository root was not found above the test assembly.");
    }
}
