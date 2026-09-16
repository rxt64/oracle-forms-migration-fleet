// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Fleet.Execution.Adapters;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// The database converter reads SQL text, so it can honestly convert an export from any Oracle release it
/// recognises. These tests hold it to saying exactly that in the report: what it proves is the constructs
/// present in the supplied export, never that a release it never connected to is supported.
/// </summary>
public class DatabaseVersionReportingTests
{
    private const string Operator = "migration-operator@contoso.com";
    private const string ReportPath = "out/orders/database/postgresql/conversion-report.md";
    private const string DdlPath = "out/orders/database/postgresql/schema/schema.sql";

    private static MigrationRunRequest Request(string databaseVersion) => new()
    {
        EngagementId = "ENG-DB",
        ApplicationName = "ORDERS",
        RequestedMode = ExecutionMode.GenerateArtifacts,
        Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
        OracleDatabaseVersion = databaseVersion,
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
    };

    private static TemporaryWorkspace SeededWorkspace()
    {
        TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/001_schema.sql", OracleSamples.Schema);
        return workspace;
    }

    private static async Task<PhaseOutcome> ConvertAsync(TemporaryWorkspace workspace, string databaseVersion)
    {
        MigrationExecutionResult result = await new MigrationExecutor(workspace.Root, [new DatabaseConversionAdapter()])
            .ExecuteAsync(Request(databaseVersion), Operator);

        return result.Phases.Single(phase => phase.Phase == MigrationPhase.DatabaseConversion);
    }

    [Theory]
    [InlineData("6", "6")]
    [InlineData("7", "7")]
    [InlineData("8i", "8i")]
    [InlineData("9i", "9i")]
    [InlineData("10g", "10g")]
    [InlineData("11g", "11g")]
    [InlineData("12c", "12c")]
    [InlineData("23ai", "23ai")]
    public async Task A_recognised_release_converts_and_the_report_names_it(string supplied, string family)
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        PhaseOutcome outcome = await ConvertAsync(workspace, supplied);

        Assert.Equal(PhaseExecutionState.Executed, outcome.State);
        Assert.True(workspace.Exists(DdlPath));

        string report = workspace.Read(ReportPath);
        Assert.Contains("## Source Oracle Database release", report, StringComparison.Ordinal);
        Assert.Contains(family, report, StringComparison.Ordinal);
        Assert.Contains("TextEvidenceReady", report, StringComparison.Ordinal);

        // The honest limit has to travel with the claim, not sit in a separate document.
        Assert.Contains("no live Oracle extraction adapter", report, StringComparison.Ordinal);
        Assert.Contains("does not establish support for every construct", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_release_is_a_warning_and_still_converts_verified_sql()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        PhaseOutcome outcome = await ConvertAsync(workspace, "unknown");

        Assert.Equal(PhaseExecutionState.Executed, outcome.State);
        Assert.True(workspace.Exists(DdlPath));

        string report = workspace.Read(ReportPath);
        Assert.Contains("Unknown", report, StringComparison.Ordinal);
        Assert.Contains("never established", report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("latest")]
    [InlineData("99")]
    [InlineData("12c 19c")]
    [InlineData("Oracle Database 19c; drop table users")]
    public async Task An_uninterpretable_release_is_refused_at_intake_before_anything_is_written(string supplied)
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        MigrationRunPlan plan = MigrationRunPlanner.Plan(Request(supplied));

        Assert.Empty(plan.Phases);
        Assert.Contains(plan.Blockers, blocker => blocker.Contains("OracleDatabaseVersion", StringComparison.Ordinal));

        MigrationExecutionResult result = await new MigrationExecutor(workspace.Root, [new DatabaseConversionAdapter()])
            .ExecuteAsync(Request(supplied), Operator);

        Assert.Empty(result.Phases);
        Assert.Empty(result.Artifacts);
        Assert.False(workspace.Exists(DdlPath));
        Assert.False(workspace.Exists(ReportPath));
    }

    [Fact]
    public async Task An_uninterpretable_release_still_stops_the_adapter_if_it_is_reached_directly()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        MigrationRunRequest request = Request("19c");
        PhasePlan plan = MigrationRunPlanner.Plan(request).Phases.Single(phase => phase.Phase == MigrationPhase.DatabaseConversion);

        PhaseExecutionResult result = await new DatabaseConversionAdapter().ExecuteAsync(
            new PhaseExecutionContext(
                workspace.Root, request.SourceRoot, request.OutputRoot, plan, request with { OracleDatabaseVersion = "banana" }, (_, _) => { }),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Nothing was converted and nothing was written", result.FailureReason!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(DdlPath));
        Assert.False(workspace.Exists(ReportPath));
    }

    /// <summary>
    /// The converter used to test the concatenated source against a fixed list of construct signatures, so
    /// an object it had no signature for produced no DDL, no finding, and no failure.
    /// </summary>
    [Theory]
    [InlineData("6", "CREATE MATERIALIZED VIEW BANK_BALANCE_MV AS SELECT 1 FROM DUAL;", "CREATE MATERIALIZED VIEW")]
    [InlineData("12c", "CREATE TYPE ACCOUNT_T AS OBJECT (ID NUMBER);", "CREATE TYPE")]
    [InlineData("12c", "CREATE DATABASE LINK CORE_DBLINK CONNECT TO REPORTING USING 'CORE';", "CREATE DATABASE LINK")]
    [InlineData("11g", "CREATE SYNONYM BANK_ACCT FOR BANK_ACCOUNT;", "CREATE SYNONYM")]
    public async Task An_unclassified_schema_bearing_statement_fails_the_conversion_and_is_named(
        string databaseVersion, string statement, string kind)
    {
        using TemporaryWorkspace workspace = SeededWorkspace();
        workspace.WriteFile("legacy/forms/db/900_extra.sql", statement + "\n");

        PhaseOutcome outcome = await ConvertAsync(workspace, databaseVersion);

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains(kind, outcome.Detail!, StringComparison.Ordinal);
        Assert.Contains("900_extra.sql", outcome.Detail!, StringComparison.Ordinal);

        // The DDL and the report stay on disk: they are the evidence for the refusal.
        Assert.True(workspace.Exists(DdlPath));
        Assert.True(workspace.Exists(ReportPath));
        Assert.Contains(outcome.Artifacts, artifact => artifact.Path == ReportPath);

        string report = workspace.Read(ReportPath);
        Assert.Contains("## Unparsed statement accounting", report, StringComparison.Ordinal);
        Assert.Contains("OmittedSchemaBearing", report, StringComparison.Ordinal);
        Assert.Contains("900_extra.sql", report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("6")]
    [InlineData("12c")]
    public async Task Comments_and_sql_plus_directives_are_accounted_for_without_failing_the_conversion(string databaseVersion)
    {
        using TemporaryWorkspace workspace = SeededWorkspace();
        workspace.WriteFile(
            "legacy/forms/db/900_directives.sql",
            """
            -- A plain comment about the estate.
            /* A block comment spanning
               more than one line. */
            SET DEFINE OFF;
            WHENEVER SQLERROR EXIT FAILURE;
            ALTER SESSION SET CURRENT_SCHEMA = BANKING;
            COMMENT ON TABLE BANK_ACCOUNT IS 'Customer accounts';
            GRANT SELECT ON BANK_ACCOUNT TO REPORTING;
            INSERT INTO BANK_ACCOUNT (ACCOUNT_ID) VALUES (1);
            COMMIT;
            """);

        PhaseOutcome outcome = await ConvertAsync(workspace, databaseVersion);

        Assert.Equal(PhaseExecutionState.Executed, outcome.State);

        string report = workspace.Read(ReportPath);
        Assert.Contains("## Unparsed statement accounting", report, StringComparison.Ordinal);
        Assert.DoesNotContain("OmittedSchemaBearing", report, StringComparison.Ordinal);

        // Nothing is silent: each one is still reported against the file it came from.
        Assert.Contains(outcome.Findings, finding => finding.Contains("ClientDirective", StringComparison.Ordinal));
        Assert.Contains(outcome.Findings, finding => finding.Contains("Administrative", StringComparison.Ordinal));
        Assert.Contains(outcome.Findings, finding => finding.Contains("DataStatement", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_known_program_unit_is_not_reported_twice()
    {
        using TemporaryWorkspace workspace = SeededWorkspace();
        workspace.WriteFile("legacy/forms/db/003_plsql.sql", OracleSamples.PlSql);

        PhaseOutcome outcome = await ConvertAsync(workspace, "12c");

        Assert.Equal(PhaseExecutionState.Executed, outcome.State);
        Assert.DoesNotContain(outcome.Findings, finding => finding.Contains("ClassifiedProgramUnit", StringComparison.Ordinal));
    }
}

/// <summary>Source analysis publishes the releases a run is working from, and what each one rests on.</summary>
public class SourceAnalysisVersionReportingTests
{
    private const string Operator = "migration-operator@contoso.com";

    [Fact]
    public async Task The_inventory_separates_the_intake_value_from_what_the_source_declares()
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/001_schema.sql", OracleSamples.Schema);
        workspace.WriteBytes("legacy/forms/ui/ACCOUNT_OPEN.fmb", [0x0A, 0x46, 0x4F, 0x52, 0x4D]);
        workspace.WriteFile(
            "legacy/forms/ui/ACCOUNT_LIST.xml",
            """
            <Module xmlns="http://xmlns.oracle.com/Forms" version="6.0.8.28" FormsVersion="6.0.8.28">
              <FormModule Name="ACCOUNT_LIST" Title="Accounts"/>
            </Module>
            """);

        MigrationExecutionResult result = await new MigrationExecutor(workspace.Root, [new SourceAnalysisAdapter()])
            .ExecuteAsync(
                new MigrationRunRequest
                {
                    EngagementId = "ENG-ANALYSIS",
                    ApplicationName = "ORDERS",
                    RequestedMode = ExecutionMode.GenerateArtifacts,
                    Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
                    OracleFormsVersion = "6i",
                    OracleDatabaseVersion = "8i",
                    SourceRoot = "legacy/forms",
                    OutputRoot = "out/orders",
                    Evidence =
                    [
                        Requests.Evidence("EV-XML", EvidenceKind.FormsXmlExport),
                        Requests.Evidence("EV-PLSQL", EvidenceKind.PlSqlProgramUnit),
                        Requests.Evidence("EV-SCHEMA", EvidenceKind.DatabaseSchemaExport),
                    ],
                    PlanApproval = Requests.Approved("plan-owner@contoso.com"),
                },
                Operator);

        Assert.Equal(
            PhaseExecutionState.Executed,
            result.Phases.Single(phase => phase.Phase == MigrationPhase.SourceAnalysis).State);

        string inventory = workspace.Read("out/orders/analysis/APPLICATION_INVENTORY.md");
        Assert.Contains("## Releases this run is working from", inventory, StringComparison.Ordinal);
        Assert.Contains("6i", inventory, StringComparison.Ordinal);
        Assert.Contains("8i", inventory, StringComparison.Ordinal);
        Assert.Contains("ACCOUNT_LIST.xml", inventory, StringComparison.Ordinal);
        Assert.Contains("NormalizedTextRequired", inventory, StringComparison.Ordinal);

        // A binary cannot tell anyone which release built it, and the report must not imply otherwise.
        Assert.Contains("counted by name and size only", inventory, StringComparison.Ordinal);
    }
}

/// <summary>
/// The request boundary is where a malformed release has to stop. A wildcard that the catalog silently
/// shortened used to travel all the way into a generated artifact citing a release nobody supplied, so the
/// same grammar is pinned here at intake for both the Forms and the Database field.
/// </summary>
public class OracleVersionIntakeWildcardTests
{
    private const string Operator = "migration-operator@contoso.com";

    [Theory]
    [InlineData("12.2.xx")]
    [InlineData("12.2.x.999")]
    [InlineData("12.*.1")]
    [InlineData("12.2.*.*")]
    [InlineData("6.0.8.xx")]
    public void A_malformed_wildcard_stops_the_request_on_either_release_field(string supplied)
    {
        Assert.Contains(
            OracleVersionIntake.Validate(supplied, "19c"),
            error => error.StartsWith("OracleFormsVersion:", StringComparison.Ordinal));

        Assert.Contains(
            OracleVersionIntake.Validate("12c", supplied),
            error => error.StartsWith("OracleDatabaseVersion:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("12.2.1.x")]
    [InlineData("12.2.*")]
    public void A_single_terminal_wildcard_passes_intake_on_either_release_field(string supplied)
    {
        Assert.Empty(OracleVersionIntake.Validate(supplied, "19c"));
        Assert.Empty(OracleVersionIntake.Validate("12c", supplied));
    }

    [Theory]
    [InlineData("12.2.xx")]
    [InlineData("6.0.8.xx")]
    public async Task A_malformed_wildcard_reaches_no_adapter_and_writes_no_artifact(string supplied)
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/001_schema.sql", OracleSamples.Schema);

        MigrationExecutionResult result = await new MigrationExecutor(workspace.Root, [new DatabaseConversionAdapter()])
            .ExecuteAsync(
                new MigrationRunRequest
                {
                    EngagementId = "ENG-WILDCARD",
                    ApplicationName = "ORDERS",
                    RequestedMode = ExecutionMode.GenerateArtifacts,
                    Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
                    OracleFormsVersion = "12c",
                    OracleDatabaseVersion = supplied,
                    SourceRoot = "legacy/forms",
                    OutputRoot = "out/orders",
                    Evidence =
                    [
                        Requests.Evidence("EV-PLSQL", EvidenceKind.PlSqlProgramUnit),
                        Requests.Evidence("EV-SCHEMA", EvidenceKind.DatabaseSchemaExport),
                        Requests.Evidence("EV-TEST", EvidenceKind.TestBaseline),
                    ],
                    PlanApproval = Requests.Approved("plan-owner@contoso.com"),
                },
                Operator);

        Assert.Empty(result.Artifacts);
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, "out")));
        Assert.DoesNotContain(result.Phases, phase => phase.State == PhaseExecutionState.Executed);
    }
}
