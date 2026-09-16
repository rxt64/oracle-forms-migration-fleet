// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Fleet.Execution.Adapters;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// The intermediate representation lives in an operator's session workspace beside source that came from a
/// customer repository, so it is untrusted input. Every field the normalization phase writes is read back
/// and checked here, because the fields that were written and never verified — where a module came from and
/// which release it declared — are exactly the ones a generated artifact is attributed to.
/// </summary>
public class FormsIntermediateReaderTests
{
    /// <summary>The source root the valid representation records, which is the run's active source root.</summary>
    private const string ActiveSourceRoot = "legacy/forms";

    /// <summary>An intermediate representation in the exact shape the normalization phase writes.</summary>
    private const string ValidIr = """
        {
          "generator": "oracle-forms-migration-fleet/source-normalization",
          "schemaVersion": "1",
          "normalized": true,
          "sourceRoot": "legacy/forms",
          "formsFamily": "12c",
          "versionAuthority": "declared by the supplied export",
          "modules": [
            {
              "name": "ORDER_ENTRY",
              "title": "Order entry",
              "sourcePath": "legacy/forms/ui/ORDER_ENTRY.xml",
              "declaredVersion": "12.2.1.4",
              "declaredFamily": "12c",
              "blocks": [
                {
                  "name": "ORDER_BLOCK",
                  "baseTable": "BANK_ACCOUNT",
                  "recordsDisplayed": 10,
                  "items": [
                    {
                      "name": "ACCOUNT_ID",
                      "itemType": "Text Item",
                      "dataType": "Number",
                      "columnName": "ACCOUNT_ID",
                      "prompt": "Account",
                      "maxLength": 12,
                      "required": true,
                      "visible": true
                    }
                  ],
                  "triggers": ["WHEN-VALIDATE-ITEM"]
                }
              ],
              "triggers": ["WHEN-NEW-FORM-INSTANCE"],
              "programUnits": [],
              "lovs": []
            }
          ],
          "notes": []
        }
        """;

    private static string Mutate(string find, string replace) =>
        ValidIr.Replace(find, replace, StringComparison.Ordinal) is { } mutated && !string.Equals(mutated, ValidIr, StringComparison.Ordinal)
            ? mutated
            : throw new InvalidOperationException($"The fragment '{find}' does not appear in the valid intermediate representation.");

    /// <summary>
    /// The representation normalization writes for an estate carrying one module name in two directories,
    /// which is two modules rather than a clash.
    /// </summary>
    private const string TwoDirectories = """
        {
          "generator": "oracle-forms-migration-fleet/source-normalization",
          "schemaVersion": "1",
          "normalized": true,
          "sourceRoot": "legacy/forms",
          "formsFamily": "12c",
          "versionAuthority": "declared by the supplied export",
          "modules": [
            {
              "name": "ORDERS",
              "title": "Orders",
              "sourcePath": "legacy/forms/forms-a/ORDERS.xml",
              "declaredVersion": "12.2.1.4",
              "declaredFamily": "12c",
              "blocks": [
                {
                  "name": "ORDER_BLOCK",
                  "baseTable": "BANK_ACCOUNT",
                  "recordsDisplayed": 10,
                  "items": [
                    {
                      "name": "ACCOUNT_ID",
                      "itemType": "Text Item",
                      "columnName": "ACCOUNT_ID",
                      "prompt": "Account",
                      "required": true,
                      "visible": true
                    }
                  ],
                  "triggers": []
                }
              ],
              "triggers": [],
              "programUnits": [],
              "lovs": []
            },
            {
              "name": "ORDERS",
              "title": "Orders",
              "sourcePath": "legacy/forms/forms-b/ORDERS.xml",
              "declaredVersion": "12.2.1.4",
              "declaredFamily": "12c",
              "blocks": [
                {
                  "name": "ORDER_BLOCK",
                  "baseTable": "BANK_ACCOUNT",
                  "recordsDisplayed": 10,
                  "items": [
                    {
                      "name": "ACCOUNT_ID",
                      "itemType": "Text Item",
                      "columnName": "ACCOUNT_ID",
                      "prompt": "Account",
                      "required": true,
                      "visible": true
                    }
                  ],
                  "triggers": []
                }
              ],
              "triggers": [],
              "programUnits": [],
              "lovs": []
            }
          ],
          "notes": []
        }
        """;

    /// <summary>Reads against the source root this run is executing on, which is what the adapter passes.</summary>
    private static FormsIntermediateRead Read(string? json) => FormsIntermediateReader.Read(json, ActiveSourceRoot);

    [Fact]
    public void The_shape_the_normalization_phase_writes_is_read_in_full()
    {
        FormsIntermediateRead read = Read(ValidIr);

        Assert.Null(read.Error);
        FormsModule module = Assert.Single(read.Modules!);

        Assert.Equal("ORDER_ENTRY", module.Name);
        Assert.Equal("Order entry", module.Title);

        FormsBlock block = Assert.Single(module.Blocks);
        Assert.Equal("BANK_ACCOUNT", block.BaseTable);
        Assert.Equal(10, block.RecordsDisplayed);

        FormsItem item = Assert.Single(block.Items);
        Assert.Equal("Number", item.DataType);
        Assert.Equal("Account", item.Prompt);
        Assert.Equal(12, item.MaxLength);
    }

    /// <summary>
    /// Where a module was read from and which release it declared were written by the producer and never
    /// checked, so a document could attribute a generated screen to a file outside the workspace, to no
    /// file at all, or to a release the document as a whole contradicts.
    /// </summary>
    [Theory]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"\"", "no non-empty 'sourcePath'")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"   \"", "no non-empty 'sourcePath'")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\",", "", "no non-empty 'sourcePath'")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": 7", "no non-empty 'sourcePath'")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"../../etc/passwd\"", "must not traverse outside the workspace")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"legacy/../../secrets.xml\"", "must not traverse outside the workspace")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"C:/windows/ORDER_ENTRY.xml\"", "must be workspace-relative")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"/etc/ORDER_ENTRY.xml\"", "must be workspace-relative")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"https://contoso/ORDER_ENTRY.xml\"", "must be workspace-relative")]
    public void A_module_that_cannot_be_attributed_to_a_source_file_is_refused(string find, string replace, string expected)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains(expected, read.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"declaredFamily\": \"12c\",", "", "no non-empty 'declaredFamily'")]
    [InlineData("\"declaredFamily\": \"12c\"", "\"declaredFamily\": \"\"", "no non-empty 'declaredFamily'")]
    [InlineData("\"declaredFamily\": \"12c\"", "\"declaredFamily\": 12", "no non-empty 'declaredFamily'")]
    [InlineData("\"declaredFamily\": \"12c\"", "\"declaredFamily\": \"banana\"", "is not a family name this catalog recognizes")]
    [InlineData("\"declaredFamily\": \"12c\"", "\"declaredFamily\": \"12.2.1.4\"", "is not a family name this catalog recognizes")]
    [InlineData("\"declaredFamily\": \"12c\"", "\"declaredFamily\": \"6i\"", "while the normalized Forms representation records '12c'")]
    [InlineData("\"formsFamily\": \"12c\"", "\"formsFamily\": \"banana\"", "is not a family name this catalog recognizes")]
    [InlineData("\"formsFamily\": \"12c\"", "\"formsFamily\": \"\"", "no non-empty 'formsFamily'")]
    [InlineData("\"formsFamily\": \"12c\"", "\"formsFamily\": 12", "no non-empty 'formsFamily'")]
    public void A_module_whose_declared_family_is_absent_unrecognized_or_contradictory_is_refused(string find, string replace, string expected)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains(expected, read.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"declaredVersion\": \"12.2.1.4\"", "\"declaredVersion\": 12", "is present and is not a string")]
    [InlineData("\"declaredVersion\": \"12.2.1.4\"", "\"declaredVersion\": [\"12.2.1.4\"]", "is present and is not a string")]
    [InlineData("\"declaredVersion\": \"12.2.1.4\"", "\"declaredVersion\": \"\"", "declares an empty 'declaredVersion'")]
    [InlineData("\"declaredVersion\": \"12.2.1.4\"", "\"declaredVersion\": \"banana\"", "matches no release this catalog knows")]
    [InlineData("\"declaredVersion\": \"12.2.1.4\"", "\"declaredVersion\": \"6.0.8.28\"", "which this catalog reads as family '6i'")]
    public void A_declared_version_that_is_mistyped_or_uninterpretable_is_refused(string find, string replace, string expected)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains(expected, read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An export that declared no version is written with no version and the unknown family, which is the
    /// one case where a module's family may differ from the release the run settled on.
    /// </summary>
    [Fact]
    public void An_export_that_declared_no_version_reads_as_the_unknown_family()
    {
        string json = Mutate("\"declaredVersion\": \"12.2.1.4\",", string.Empty)
            .Replace("\"declaredFamily\": \"12c\"", "\"declaredFamily\": \"unknown\"", StringComparison.Ordinal);

        FormsIntermediateRead read = Read(json);

        Assert.Null(read.Error);
        Assert.Single(read.Modules!);
    }

    [Fact]
    public void A_null_declared_version_reads_as_no_version_rather_than_a_refusal()
    {
        FormsIntermediateRead read = Read(
            Mutate("\"declaredVersion\": \"12.2.1.4\"", "\"declaredVersion\": null")
                .Replace("\"declaredFamily\": \"12c\"", "\"declaredFamily\": \"unknown\"", StringComparison.Ordinal));

        Assert.Null(read.Error);
        Assert.Single(read.Modules!);
    }

    /// <summary>
    /// A maximum length supplied as text, as a fraction, or beyond the 32-bit range used to read back as no
    /// limit at all, so the generated field silently accepted input the source module bounded.
    /// </summary>
    [Theory]
    [InlineData("\"maxLength\": \"12\"")]
    [InlineData("\"maxLength\": 12.5")]
    [InlineData("\"maxLength\": -1")]
    [InlineData("\"maxLength\": 99999999999")]
    [InlineData("\"maxLength\": true")]
    [InlineData("\"maxLength\": [12]")]
    public void A_malformed_maximum_length_is_refused_rather_than_read_as_no_limit(string replacement)
    {
        FormsIntermediateRead read = Read(Mutate("\"maxLength\": 12", replacement));

        Assert.Null(read.Modules);
        Assert.Contains("'maxLength'", read.Error!, StringComparison.Ordinal);
        Assert.Contains("non-negative whole number", read.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"maxLength\": 12,", "")]
    [InlineData("\"maxLength\": 12", "\"maxLength\": null")]
    [InlineData("\"maxLength\": 12", "\"maxLength\": 0")]
    public void An_absent_null_or_zero_maximum_length_is_read_rather_than_refused(string find, string replace)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Error);
        Assert.Single(read.Modules!);
    }

    /// <summary>
    /// A wrongly typed optional string used to read back as null, so a prompt, a column binding, or a base
    /// table supplied as a number disappeared from the generated artifact without a word.
    /// </summary>
    [Theory]
    [InlineData("\"title\": \"Order entry\"", "\"title\": true")]
    [InlineData("\"title\": \"Order entry\"", "\"title\": 7")]
    [InlineData("\"baseTable\": \"BANK_ACCOUNT\"", "\"baseTable\": []")]
    [InlineData("\"baseTable\": \"BANK_ACCOUNT\"", "\"baseTable\": 7")]
    [InlineData("\"dataType\": \"Number\"", "\"dataType\": 1")]
    [InlineData("\"columnName\": \"ACCOUNT_ID\"", "\"columnName\": {}")]
    [InlineData("\"prompt\": \"Account\"", "\"prompt\": 7")]
    public void A_wrongly_typed_optional_string_is_refused_rather_than_dropped(string find, string replace)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains("is present and is not a string", read.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"title\": \"Order entry\"", "\"title\": null")]
    [InlineData("\"baseTable\": \"BANK_ACCOUNT\"", "\"baseTable\": null")]
    [InlineData("\"prompt\": \"Account\"", "\"prompt\": null")]
    public void A_null_optional_string_is_read_as_absent(string find, string replace)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Error);
        Assert.Single(read.Modules!);
    }

    [Theory]
    [InlineData("\"required\": true", "\"required\": \"true\"", "is not a boolean")]
    [InlineData("\"required\": true", "\"required\": 1", "is not a boolean")]
    [InlineData("\"visible\": true", "\"visible\": null", "is not a boolean")]
    [InlineData("\"recordsDisplayed\": 10", "\"recordsDisplayed\": \"10\"", "is not a non-negative whole number")]
    [InlineData("\"recordsDisplayed\": 10", "\"recordsDisplayed\": 10.5", "is not a non-negative whole number")]
    [InlineData("\"recordsDisplayed\": 10", "\"recordsDisplayed\": -1", "is not a non-negative whole number")]
    [InlineData("\"recordsDisplayed\": 10", "\"recordsDisplayed\": null", "is not a non-negative whole number")]
    public void A_wrongly_typed_boolean_or_count_is_refused(string find, string replace, string expected)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains(expected, read.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"triggers\": [\"WHEN-NEW-FORM-INSTANCE\"]", "\"triggers\": \"WHEN-NEW-FORM-INSTANCE\"", "is not an array")]
    [InlineData("\"triggers\": [\"WHEN-NEW-FORM-INSTANCE\"]", "\"triggers\": [7]", "not a non-empty string")]
    [InlineData("\"triggers\": [\"WHEN-NEW-FORM-INSTANCE\"]", "\"triggers\": [\"\"]", "not a non-empty string")]
    [InlineData("\"triggers\": [\"WHEN-NEW-FORM-INSTANCE\"]", "\"triggers\": [\"A\", null]", "not a non-empty string")]
    [InlineData("\"triggers\": [\"WHEN-VALIDATE-ITEM\"]", "\"triggers\": [[\"WHEN-VALIDATE-ITEM\"]]", "not a non-empty string")]
    [InlineData("\"programUnits\": []", "\"programUnits\": {}", "is not an array")]
    [InlineData("\"lovs\": []", "\"lovs\": [{ \"name\": \"LOV\" }]", "not a non-empty string")]
    public void A_malformed_string_array_is_refused_rather_than_partly_read(string find, string replace, string expected)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains(expected, read.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"\"")]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": 7")]
    [InlineData("\"versionAuthority\": \"declared by the supplied export\"", "\"versionAuthority\": \"\"")]
    [InlineData("\"versionAuthority\": \"declared by the supplied export\"", "\"versionAuthority\": false")]
    public void A_blank_or_mistyped_root_field_is_refused(string find, string replace)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains("source root or version authority", read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The source root was written by the producer and compared to nothing, so a representation left in a
    /// session workspace by an earlier run — or edited by hand — was read against whatever estate happened
    /// to be executing, and every screen generated from it was attributed to modules this run never
    /// normalized. It has to be the active root exactly.
    /// </summary>
    [Theory]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"legacy/other\"")]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"legacy\"")]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"legacy/forms/ui\"")]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"LEGACY/FORMS\"")]
    public void A_representation_recording_another_source_root_is_refused(string find, string replace)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains("while this run is executing against 'legacy/forms'", read.Error!, StringComparison.Ordinal);
    }

    /// <summary>A source root that is not a workspace path at all is refused before any module is read.</summary>
    [Theory]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"../../etc\"", "must not traverse outside the workspace")]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"C:/legacy/forms\"", "must be workspace-relative")]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"/legacy/forms\"", "must be workspace-relative")]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"https://contoso/forms\"", "must be workspace-relative")]
    public void A_source_root_that_is_not_a_workspace_path_is_refused(string find, string replace, string expected)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains("The 'sourceRoot' of the normalized Forms representation", read.Error!, StringComparison.Ordinal);
        Assert.Contains(expected, read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A module path inside the workspace but outside the root this run normalized belongs to a different
    /// estate. The containment check is on whole segments, so a sibling directory whose name merely begins
    /// with the root's does not pass as one beneath it.
    /// </summary>
    [Theory]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"legacy/other/ORDER_ENTRY.xml\"")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"legacy/forms-b/ORDER_ENTRY.xml\"")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"legacy/formsX/ORDER_ENTRY.xml\"")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"ORDER_ENTRY.xml\"")]
    public void A_module_read_from_outside_the_source_root_is_refused(string find, string replace)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains("is not under the source root 'legacy/forms'", read.Error!, StringComparison.Ordinal);
    }

    /// <summary>The root itself, and any path beneath it, are the paths a module may be attributed to.</summary>
    [Theory]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"legacy/forms/ORDER_ENTRY.xml\"")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"legacy/forms/a/b/c/ORDER_ENTRY.xml\"")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"legacy\\\\forms\\\\ui\\\\ORDER_ENTRY.xml\"")]
    public void A_module_read_from_under_the_source_root_is_accepted(string find, string replace)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Error);
        Assert.Single(read.Modules!);
    }

    /// <summary>
    /// Two directories carrying one module name carry two modules. Normalization accepts that estate when
    /// each directory supplies its own export, so refusing it here on the bare name would reject a
    /// representation this fleet had just written.
    /// </summary>
    [Fact]
    public void Two_modules_of_one_name_from_separate_directories_are_both_read()
    {
        FormsIntermediateRead read = Read(TwoDirectories);

        Assert.Null(read.Error);
        Assert.Equal(2, read.Modules!.Count);
        Assert.All(read.Modules!, module => Assert.Equal("ORDERS", module.Name));
        Assert.Equal(
            ["legacy/forms/forms-a/ORDERS", "legacy/forms/forms-b/ORDERS"],
            read.Modules!.Select(module => module.QualifiedName).Order(StringComparer.Ordinal));
    }

    /// <summary>Directory scoping must not loosen identity inside one directory.</summary>
    [Fact]
    public void Two_modules_of_one_name_from_one_directory_are_refused()
    {
        FormsIntermediateRead read = Read(TwoDirectories.Replace(
            "\"sourcePath\": \"legacy/forms/forms-b/ORDERS.xml\"",
            "\"sourcePath\": \"legacy/forms/forms-a/ORDERS_COPY.xml\"",
            StringComparison.Ordinal));

        Assert.Null(read.Modules);
        Assert.Contains("legacy/forms/forms-a/ORDERS", read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal has to reach the caller: a damaged representation that still generated an application
    /// would present CRUD over the converted tables as a migration of modules nothing read.
    /// </summary>
    [Theory]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"../../etc/passwd\"")]
    [InlineData("\"declaredFamily\": \"12c\",", "")]
    [InlineData("\"declaredFamily\": \"12c\"", "\"declaredFamily\": \"6i\"")]
    [InlineData("\"maxLength\": 12", "\"maxLength\": \"12\"")]
    [InlineData("\"prompt\": \"Account\"", "\"prompt\": 7")]
    [InlineData("\"triggers\": [\"WHEN-NEW-FORM-INSTANCE\"]", "\"triggers\": [7]")]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"legacy/other\"")]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"legacy\"")]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"/legacy/forms\"")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"legacy/forms-b/ORDER_ENTRY.xml\"")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"legacy/other/ORDER_ENTRY.xml\"")]
    public async Task Application_conversion_refuses_a_damaged_representation_and_writes_no_application_file(string find, string replace)
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/001_schema.sql", OracleSamples.Schema);
        workspace.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", OracleSamples.FormsXml("12.2.1.4"));
        workspace.WriteFile("out/orders/intermediate/forms-ir.json", Mutate(find, replace));

        PhaseExecutionResult result = await ConvertAsync(workspace);

        Assert.False(result.Succeeded);
        Assert.Contains("was refused", result.FailureReason!, StringComparison.Ordinal);
        Assert.Empty(ApplicationFiles(workspace));
    }

    [Fact]
    public async Task Application_conversion_reads_the_representation_this_fleet_writes()
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/001_schema.sql", OracleSamples.Schema);
        workspace.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", OracleSamples.FormsXml("12.2.1.4"));
        workspace.WriteFile("out/orders/intermediate/forms-ir.json", ValidIr);

        PhaseExecutionResult result = await ConvertAsync(workspace);

        Assert.True(result.Succeeded);
        Assert.NotEmpty(ApplicationFiles(workspace));
    }

    private static IReadOnlyList<string> ApplicationFiles(TemporaryWorkspace workspace)
    {
        string directory = workspace.Absolute("out/orders/application");

        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            : [];
    }

    private static Task<PhaseExecutionResult> ConvertAsync(TemporaryWorkspace workspace)
    {
        MigrationRunRequest request = new()
        {
            EngagementId = "ENG-IR",
            ApplicationName = "ORDERS",
            RequestedMode = ExecutionMode.GenerateArtifacts,
            Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
            OracleFormsVersion = "12c",
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
}
