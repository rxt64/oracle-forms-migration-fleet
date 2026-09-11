// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Tests;

public class OracleSchemaParserTests
{
    private static OracleSchema Parsed() => OracleSchemaParser.Parse(OracleSamples.Schema);

    private static OracleTable Table(OracleSchema schema, string name) =>
        schema.Tables.Single(table => table.Name == name);

    [Fact]
    public void Parses_every_create_table_including_lower_case_statements()
    {
        OracleSchema schema = Parsed();

        Assert.Equal(
            ["BANK_ACCOUNT_REQUEST", "BANK_ACCOUNT", "BANK_TRANSACTION"],
            schema.Tables.Select(table => table.Name));
    }

    [Fact]
    public void Parses_columns_with_precision_scale_nullability_and_defaults()
    {
        OracleTable request = Table(Parsed(), "BANK_ACCOUNT_REQUEST");

        OracleColumn status = request.Columns.Single(column => column.Name == "REQUEST_STATUS");
        Assert.Equal("VARCHAR2", status.BaseType);
        Assert.Equal(12, status.Precision);
        Assert.True(status.NotNull);
        Assert.Equal("'SUBMITTED'", status.Default);

        OracleColumn submitted = request.Columns.Single(column => column.Name == "SUBMITTED_AT");
        Assert.Equal("TIMESTAMP", submitted.BaseType);
        Assert.Equal("SYSTIMESTAMP", submitted.Default);

        OracleColumn decided = request.Columns.Single(column => column.Name == "DECIDED_AT");
        Assert.False(decided.NotNull);
        Assert.Null(decided.Default);

        OracleColumn amount = Table(Parsed(), "BANK_TRANSACTION").Columns.Single(column => column.Name == "AMOUNT");
        Assert.Equal("NUMBER", amount.BaseType);
        Assert.Equal(12, amount.Precision);
        Assert.Equal(2, amount.Scale);
    }

    [Fact]
    public void Parses_inline_primary_keys_and_named_out_of_line_constraints()
    {
        OracleTable account = Table(Parsed(), "BANK_ACCOUNT");

        OracleConstraint primaryKey = account.Constraints.Single(c => c.Kind == OracleConstraintKind.PrimaryKey);
        Assert.Equal(["ACCOUNT_ID"], primaryKey.Columns);

        OracleConstraint unique = account.Constraints.Single(c => c.Kind == OracleConstraintKind.Unique);
        Assert.Equal("BANK_ACCT_REQUEST_UQ", unique.Name);
        Assert.Equal(["REQUEST_ID"], unique.Columns);
    }

    [Fact]
    public void Parses_foreign_keys_with_referenced_table_and_columns()
    {
        OracleConstraint foreignKey = Table(Parsed(), "BANK_TRANSACTION")
            .Constraints.Single(c => c.Kind == OracleConstraintKind.ForeignKey);

        Assert.Equal("BANK_TXN_ACCOUNT_FK", foreignKey.Name);
        Assert.Equal(["ACCOUNT_ID"], foreignKey.Columns);
        Assert.Equal("BANK_ACCOUNT", foreignKey.ReferencedTable);
        Assert.Equal(["ACCOUNT_ID"], foreignKey.ReferencedColumns);
    }

    [Fact]
    public void Keeps_check_expressions_verbatim_including_embedded_commas()
    {
        OracleConstraint check = Table(Parsed(), "BANK_ACCOUNT_REQUEST")
            .Constraints.Single(c => c.Name == "BANK_REQ_KIND_CK");

        Assert.Equal(OracleConstraintKind.Check, check.Kind);
        Assert.Equal("ACCOUNT_KIND IN ('SAVINGS', 'CHECKING')", check.CheckExpression);
    }

    [Fact]
    public void Parses_sequences_and_indexes()
    {
        OracleSchema schema = Parsed();

        OracleSequence sequence = schema.Sequences.Single(s => s.Name == "BANK_ACCOUNT_SEQ");
        Assert.Equal(500001, sequence.StartWith);
        Assert.Equal(1, sequence.IncrementBy);

        OracleIndex index = Assert.Single(schema.Indexes);
        Assert.Equal("BANK_TXN_ACCOUNT_IX", index.Name);
        Assert.Equal("BANK_TRANSACTION", index.Table);
        Assert.Equal(["ACCOUNT_ID", "TRANSACTION_TS"], index.Columns);
        Assert.False(index.IsUnique);
    }

    [Fact]
    public void Records_unrecognised_statements_instead_of_throwing()
    {
        OracleSchema schema = Parsed();

        Assert.Contains(schema.Unparsed, statement => statement.StartsWith("WHENEVER", StringComparison.Ordinal));
        Assert.Contains(schema.Unparsed, statement => statement.Contains("ALTER SESSION", StringComparison.Ordinal));
        Assert.Contains(schema.Unparsed, statement => statement.StartsWith("SET DEFINE", StringComparison.Ordinal));
    }

    [Fact]
    public void Keeps_a_plsql_package_body_whole_rather_than_splitting_it_on_semicolons()
    {
        OracleSchema schema = OracleSchemaParser.Parse(OracleSamples.PlSql);

        string body = Assert.Single(schema.Unparsed);
        Assert.StartsWith("CREATE OR REPLACE PACKAGE BODY", body, StringComparison.Ordinal);
        Assert.Contains("END LEGACY_BANKING_API;", body, StringComparison.Ordinal);
        Assert.Empty(schema.Tables);
    }

    [Fact]
    public void Tolerates_comments_blank_lines_and_statements_spanning_lines()
    {
        OracleSchema schema = OracleSchemaParser.Parse("""

            -- leading comment with a semicolon; and a quote'
            /* block
               comment */
            CREATE TABLE ledger
            (
                id
                    NUMBER(10)
                        NOT NULL
            );
            """);

        OracleTable table = Assert.Single(schema.Tables);
        Assert.Equal("ledger", table.Name);
        OracleColumn column = Assert.Single(table.Columns);
        Assert.Equal("id", column.Name);
        Assert.True(column.NotNull);
    }

    [Fact]
    public void Attaches_alter_table_add_constraint_to_the_table_it_names()
    {
        OracleSchema schema = OracleSchemaParser.Parse("""
            CREATE TABLE PARENT (ID NUMBER(9) PRIMARY KEY);
            CREATE TABLE CHILD (ID NUMBER(9), PARENT_ID NUMBER(9));
            ALTER TABLE BANKING.CHILD ADD CONSTRAINT CHILD_FK FOREIGN KEY (PARENT_ID) REFERENCES PARENT (ID);
            """);

        OracleConstraint foreignKey = schema.Tables
            .Single(table => table.Name == "CHILD")
            .Constraints.Single(c => c.Kind == OracleConstraintKind.ForeignKey);

        Assert.Equal("CHILD_FK", foreignKey.Name);
        Assert.Equal("PARENT", foreignKey.ReferencedTable);
        Assert.Empty(schema.Unparsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_input_yields_an_empty_schema(string? input)
    {
        OracleSchema schema = OracleSchemaParser.Parse(input);

        Assert.Empty(schema.Tables);
        Assert.Empty(schema.Sequences);
        Assert.Empty(schema.Indexes);
        Assert.Empty(schema.Unparsed);
    }
}
