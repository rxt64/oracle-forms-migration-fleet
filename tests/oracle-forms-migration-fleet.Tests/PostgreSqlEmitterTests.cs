// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Tests;

public class PostgreSqlEmitterTests
{
    private static PostgreSqlConversion Convert(string? oracleText = null)
    {
        string text = oracleText ?? OracleSamples.Schema;
        return PostgreSqlEmitter.Convert(OracleSchemaParser.Parse(text), text);
    }

    private static string ColumnType(string column)
    {
        PostgreSqlConversion conversion = PostgreSqlEmitter.Convert(
            OracleSchemaParser.Parse($"CREATE TABLE PROBE (VALUE_COLUMN {column});"));

        return conversion.Report.TypeMappings.Single().PostgreSqlType;
    }

    [Theory]
    [InlineData("NUMBER(10)", "bigint")]
    [InlineData("NUMBER(9)", "integer")]
    [InlineData("NUMBER(12, 2)", "numeric(12,2)")]
    [InlineData("NUMBER", "numeric")]
    [InlineData("NUMBER(30)", "numeric(30)")]
    [InlineData("VARCHAR2(40)", "varchar(40)")]
    [InlineData("VARCHAR2(40 CHAR)", "varchar(40)")]
    [InlineData("NVARCHAR2(40)", "varchar(40)")]
    [InlineData("CHAR(2)", "char(2)")]
    [InlineData("DATE", "date")]
    [InlineData("TIMESTAMP", "timestamp")]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", "timestamptz")]
    [InlineData("CLOB", "text")]
    [InlineData("BLOB", "bytea")]
    [InlineData("RAW(32)", "bytea")]
    [InlineData("LONG RAW", "bytea")]
    [InlineData("BINARY_DOUBLE", "double precision")]
    public void Oracle_types_map_to_their_postgresql_equivalents(string oracleType, string expected)
    {
        Assert.Equal(expected, ColumnType(oracleType));
    }

    [Fact]
    public void Identifiers_are_lower_cased_and_quoted_only_when_necessary()
    {
        string ddl = Convert().Ddl;

        Assert.Contains("CREATE TABLE bank_account_request (", ddl, StringComparison.Ordinal);
        Assert.Contains("    request_status varchar(12)", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("BANK_ACCOUNT_REQUEST", ddl, StringComparison.Ordinal);
        Assert.Equal("\"order\"", PostgreSqlEmitter.Identifier("ORDER"));
        Assert.Equal("\"weird name\"", PostgreSqlEmitter.Identifier("Weird Name"));
        Assert.Equal("bank_account", PostgreSqlEmitter.Identifier("BANK_ACCOUNT"));
    }

    [Fact]
    public void Oracle_pseudo_columns_in_defaults_are_translated()
    {
        string ddl = Convert().Ddl;

        Assert.Contains("opened_on date DEFAULT CURRENT_DATE NOT NULL", ddl, StringComparison.Ordinal);
        Assert.Contains("submitted_at timestamp DEFAULT now() NOT NULL", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("SYSDATE", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("SYSTIMESTAMP", ddl, StringComparison.Ordinal);

        PostgreSqlConversion guid = PostgreSqlEmitter.Convert(
            OracleSchemaParser.Parse("CREATE TABLE PROBE (ID RAW(16) DEFAULT SYS_GUID() NOT NULL);"));
        Assert.Contains("DEFAULT gen_random_uuid()", guid.Ddl, StringComparison.Ordinal);
    }

    [Fact]
    public void Sequences_are_emitted_with_start_and_increment()
    {
        Assert.Contains(
            "CREATE SEQUENCE bank_account_seq START WITH 500001 INCREMENT BY 1;",
            Convert().Ddl,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Primary_key_unique_and_check_constraints_are_preserved_and_foreign_keys_are_deferred()
    {
        string ddl = Convert().Ddl;

        Assert.Contains("PRIMARY KEY (account_id)", ddl, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT bank_acct_request_uq UNIQUE (request_id)", ddl, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT bank_req_kind_ck CHECK (ACCOUNT_KIND IN ('SAVINGS', 'CHECKING'))", ddl, StringComparison.Ordinal);
        Assert.Contains(
            "ALTER TABLE bank_transaction ADD CONSTRAINT bank_txn_account_fk FOREIGN KEY (account_id) REFERENCES bank_account (account_id);",
            ddl,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Indexes_are_emitted_against_the_lower_cased_table()
    {
        Assert.Contains(
            "CREATE INDEX bank_txn_account_ix ON bank_transaction (account_id, transaction_ts);",
            Convert().Ddl,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Every_check_constraint_is_flagged_for_manual_review()
    {
        ConversionReport report = Convert().Report;

        IReadOnlyList<ConversionFinding> checks =
            [.. report.Findings.Where(finding => finding.Category == "Check constraint")];

        Assert.Equal(4, checks.Count);
        Assert.All(checks, finding => Assert.Equal(ConversionSeverity.ManualReview, finding.Severity));
        Assert.Contains(checks, finding => finding.Construct == "BANK_ACCOUNT_REQUEST.BANK_REQ_KIND_CK");
    }

    [Fact]
    public void Raw_varchar2_date_and_number_semantics_are_reported_for_review()
    {
        ConversionReport report = Convert().Report;

        Assert.Contains(report.Findings, finding =>
            finding.Category == "RAW" && finding.Construct == "BANK_ACCOUNT.ONLINE_PASSWORD_HASH");
        Assert.Contains(report.Findings, finding => finding.Category == "VARCHAR2 semantics");
        Assert.Contains(report.Findings, finding =>
            finding.Category == "DATE semantics" && finding.Construct == "BANK_ACCOUNT.OPENED_ON");
        Assert.Contains(
            PostgreSqlEmitter.Convert(OracleSchemaParser.Parse("CREATE TABLE PROBE (V NUMBER);")).Report.Findings,
            finding => finding.Category == "NUMBER precision loss");
    }

    [Fact]
    public void Plsql_constructs_are_reported_as_manual_rewrites_and_are_never_translated()
    {
        PostgreSqlConversion conversion = Convert(OracleSamples.Schema + "\n" + OracleSamples.PlSql);

        IReadOnlyList<string> unsupported =
            [.. conversion.Report.Findings
                .Where(finding => finding.Severity == ConversionSeverity.Unsupported)
                .Select(finding => finding.Construct)];

        Assert.Contains("PACKAGE", unsupported);
        Assert.Contains("PROCEDURE", unsupported);
        Assert.Contains("FUNCTION", unsupported);
        Assert.Contains("%ROWTYPE", unsupported);
        Assert.Contains("DUAL", unsupported);
        Assert.Contains("ROWNUM", unsupported);
        Assert.Contains("STANDARD_HASH", unsupported);

        // The emitter must not invent a PL/pgSQL translation of the package body.
        Assert.DoesNotContain("LEGACY_BANKING_API", conversion.Ddl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$$", conversion.Ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE FUNCTION", conversion.Ddl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Report_counts_match_the_parsed_schema()
    {
        ConversionReport report = Convert().Report;

        Assert.Equal(3, report.Tables);
        Assert.Equal(19, report.Columns);
        Assert.Equal(2, report.Sequences);
        Assert.Equal(1, report.Indexes);
        Assert.Equal(report.Columns, report.TypeMappings.Count);
    }

    [Fact]
    public void Output_is_deterministic_and_carries_no_timestamp()
    {
        PostgreSqlConversion first = Convert();
        PostgreSqlConversion second = Convert();

        Assert.Equal(first.Ddl, second.Ddl);
        Assert.Equal(
            PostgreSqlEmitter.RenderReport(first.Report, "ORDERS"),
            PostgreSqlEmitter.RenderReport(second.Report, "ORDERS"));
        Assert.DoesNotContain(DateTime.UtcNow.Year.ToString(System.Globalization.CultureInfo.InvariantCulture), first.Ddl, StringComparison.Ordinal);
    }

    [Fact]
    public void Foreign_keys_to_tables_that_are_never_created_are_flagged()
    {
        PostgreSqlConversion conversion = PostgreSqlEmitter.Convert(OracleSchemaParser.Parse("""
            CREATE TABLE CHILD
            (
                ID         NUMBER(9) PRIMARY KEY,
                PARENT_ID  NUMBER(9) NOT NULL,
                CONSTRAINT CHILD_FK FOREIGN KEY (PARENT_ID) REFERENCES MISSING_PARENT (ID)
            );
            """));

        Assert.Contains(conversion.Report.Findings, finding =>
            finding.Severity == ConversionSeverity.ManualReview &&
            finding.Reason.Contains("MISSING_PARENT", StringComparison.Ordinal));
    }
}
