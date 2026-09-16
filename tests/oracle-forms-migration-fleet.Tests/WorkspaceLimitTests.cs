// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Fleet.Execution.Adapters;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// The intake limits used to truncate in silence: an oversized export came back as its first bytes and an
/// oversized tree came back as its first files, and every caller read both as the whole input. These tests
/// hold each phase to refusing instead, and to producing no schema, no intermediate representation, and no
/// application tier from a partial read.
/// </summary>
public class WorkspaceLimitTests
{
    /// <summary>The byte budget every execution adapter reads a single file with.</summary>
    private const long MaxTextBytes = 8L * 1024 * 1024;

    /// <summary>The file budget every execution adapter enumerates a source tree with.</summary>
    private const int MaxFiles = 20_000;

    private const string Operator = "migration-operator@contoso.com";

    private const string FormsXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Module xmlns="http://xmlns.oracle.com/Forms" version="12.2.1.4">
          <FormModule Name="ORDER_ENTRY" Title="Order entry">
            <Block Name="ORDER_BLOCK" QueryDataSourceName="BANK_ACCOUNT" RecordsDisplayCount="10">
              <Item Name="ACCOUNT_ID" ItemType="Text Item" DataType="Number" ColumnName="ACCOUNT_ID" Prompt="Account"/>
            </Block>
          </FormModule>
        </Module>
        """;

    private static MigrationRunRequest Request() => new()
    {
        EngagementId = "ENG-LIMIT",
        ApplicationName = "ORDERS",
        RequestedMode = ExecutionMode.GenerateArtifacts,
        Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
        OracleFormsVersion = "12c",
        OracleDatabaseVersion = "19c",
        SourceRoot = "legacy/forms",
        OutputRoot = "out/orders",
        Evidence =
        [
            Requests.Evidence("EV-INV", EvidenceKind.FormsModuleInventory),
            Requests.Evidence("EV-SRC", EvidenceKind.FormsModuleSource),
            Requests.Evidence("EV-XML", EvidenceKind.FormsXmlExport),
            Requests.Evidence("EV-PLSQL", EvidenceKind.PlSqlProgramUnit),
            Requests.Evidence("EV-SCHEMA", EvidenceKind.DatabaseSchemaExport),
            Requests.Evidence("EV-TEST", EvidenceKind.TestBaseline),
        ],
        PlanApproval = Requests.Approved("plan-owner@contoso.com"),
    };

    /// <summary>Runs the four phases that read the source tree, in lifecycle order, over one workspace.</summary>
    private static Task<MigrationExecutionResult> RunAsync(TemporaryWorkspace workspace) =>
        new MigrationExecutor(
            workspace.Root,
            [
                new SourceAnalysisAdapter(),
                new SourceNormalizationAdapter(),
                new ApplicationCodeConversionAdapter(),
                new DatabaseConversionAdapter(),
            ])
            .ExecuteAsync(Request(), Operator);

    private static PhaseOutcome Outcome(MigrationExecutionResult result, MigrationPhase phase) =>
        result.Phases.Single(candidate => candidate.Phase == phase);

    // ---------- the writer itself ----------

    [Fact]
    public void A_file_larger_than_the_budget_is_refused_instead_of_truncated()
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/module.xml", "<Module/>");

        WorkspaceWriter writer = new(workspace.Root);

        WorkspaceLimitExceededException refusal =
            Assert.Throws<WorkspaceLimitExceededException>(() => writer.ReadText("legacy/forms/module.xml", 4));

        Assert.Equal(WorkspaceLimitKind.FileSize, refusal.Kind);
        Assert.Equal("legacy/forms/module.xml", refusal.Path);
        Assert.Equal(4, refusal.Limit);
        Assert.Equal(9, refusal.Actual);
        Assert.False(refusal.ActualIsLowerBound);

        // The message is operator-facing, so it names the workspace-relative path and no host directory.
        Assert.DoesNotContain(workspace.Root, refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_file_exactly_at_the_budget_is_read_in_full()
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/module.xml", "<Module/>");

        Assert.Equal("<Module/>", new WorkspaceWriter(workspace.Root).ReadText("legacy/forms/module.xml", 9));
    }

    [Fact]
    public void A_tree_beyond_the_file_budget_is_refused_instead_of_listed_as_complete()
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteEmptyFiles("legacy/forms", ".sql", 4);

        WorkspaceWriter writer = new(workspace.Root);

        WorkspaceLimitExceededException refusal =
            Assert.Throws<WorkspaceLimitExceededException>(() => writer.EnumerateFiles("legacy/forms", 3));

        Assert.Equal(WorkspaceLimitKind.FileCount, refusal.Kind);
        Assert.Equal("legacy/forms", refusal.Path);
        Assert.Equal(3, refusal.Limit);
        Assert.Equal(4, refusal.Actual);
        Assert.True(refusal.ActualIsLowerBound);
    }

    [Fact]
    public void A_tree_exactly_at_the_file_budget_is_listed_in_deterministic_order()
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/z.sql", "-- z");
        workspace.WriteFile("legacy/forms/a.sql", "-- a");
        workspace.WriteFile("legacy/forms/nested/b.sql", "-- b");

        WorkspaceWriter writer = new(workspace.Root);

        Assert.Equal(
            ["legacy/forms/a.sql", "legacy/forms/nested/b.sql", "legacy/forms/z.sql"],
            writer.EnumerateFiles("legacy/forms", 3).Select(file => file.RelativePath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_budget_is_a_caller_error_rather_than_an_empty_result(int budget)
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/a.sql", "-- a");

        WorkspaceWriter writer = new(workspace.Root);

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.ReadText("legacy/forms/a.sql", budget));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.EnumerateFiles("legacy/forms", budget));
    }

    // ---------- the phases ----------

    [Fact]
    public async Task An_oversized_forms_xml_export_produces_no_normalization_and_no_application()
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/001_schema.sql", OracleSamples.Schema);
        workspace.WriteFileOfLength("legacy/forms/ui/ORDER_ENTRY.xml", FormsXml, MaxTextBytes + 1);

        MigrationExecutionResult result = await RunAsync(workspace);

        // Normalization reads the export, so it is the phase that refuses.
        PhaseOutcome normalization = Outcome(result, MigrationPhase.SourceNormalization);
        Assert.Equal(PhaseExecutionState.Failed, normalization.State);
        Assert.Contains("ORDER_ENTRY.xml", normalization.Detail!, StringComparison.Ordinal);
        Assert.Contains("was not read at all", normalization.Detail!, StringComparison.Ordinal);

        // Nothing partial survives the refusal: no manifest, no report, and above all no Forms model.
        Assert.False(workspace.Exists("out/orders/intermediate/forms-ir.json"));
        Assert.False(workspace.Exists("out/orders/intermediate/forms-normalization-manifest.json"));
        Assert.Empty(normalization.Artifacts);

        // The conversion depends on that refusal, so it may not generate an application tier either.
        PhaseOutcome conversion = Outcome(result, MigrationPhase.ApplicationCodeConversion);
        Assert.NotEqual(PhaseExecutionState.Executed, conversion.State);
        Assert.Empty(conversion.Artifacts);
    }

    [Fact]
    public async Task An_oversized_sql_script_produces_no_schema_analysis_conversion_or_application()
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFileOfLength("legacy/forms/db/001_schema.sql", OracleSamples.Schema, MaxTextBytes + 1);

        MigrationExecutionResult result = await RunAsync(workspace);

        foreach (MigrationPhase phase in (MigrationPhase[])
                 [
                     MigrationPhase.SourceAnalysis,
                     MigrationPhase.ApplicationCodeConversion,
                     MigrationPhase.DatabaseConversion,
                 ])
        {
            PhaseOutcome outcome = Outcome(result, phase);
            Assert.NotEqual(PhaseExecutionState.Executed, outcome.State);
            Assert.Empty(outcome.Artifacts);
        }

        Assert.Contains("001_schema.sql", Outcome(result, MigrationPhase.DatabaseConversion).Detail!, StringComparison.Ordinal);

        // A partial parse would have emitted DDL and an application for whichever tables fitted.
        Assert.False(Directory.Exists(workspace.Absolute("out/orders/analysis")));
        Assert.False(Directory.Exists(workspace.Absolute("out/orders/database")));
        Assert.False(Directory.Exists(workspace.Absolute("out/orders/application")));
    }

    [Fact]
    public async Task A_tree_beyond_twenty_thousand_files_produces_no_successful_phase()
    {
        using TemporaryWorkspace workspace = new();

        // One file past the limit every source-reading adapter enumerates with.
        workspace.WriteEmptyFiles("legacy/forms/bulk", ".txt", MaxFiles);
        workspace.WriteFile("legacy/forms/db/001_schema.sql", OracleSamples.Schema);

        MigrationExecutionResult result = await RunAsync(workspace);

        Assert.Equal(MaxFiles + 1, Directory.GetFiles(Path.Combine(workspace.Root, "legacy", "forms"), "*", SearchOption.AllDirectories).Length);

        Assert.All(result.Phases, outcome =>
        {
            Assert.NotEqual(PhaseExecutionState.Executed, outcome.State);
            Assert.Empty(outcome.Artifacts);
        });

        Assert.Contains("more than 20000 files", Outcome(result, MigrationPhase.SourceAnalysis).Detail!, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, "out")));
    }
}
