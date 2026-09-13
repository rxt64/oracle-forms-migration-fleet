// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Tests;

public class OracleSourceFileTests
{
    [Theory]
    [InlineData("plsql/packages/PKG_EMPLOYEE.pkb")]
    [InlineData("plsql/packages/PKG_EMPLOYEE.pks")]
    [InlineData("plsql/triggers/trg_audit.trg")]
    [InlineData("schema/tables/01_core_tables.sql")]
    public void Real_oracle_source_extensions_are_read(string path) =>
        // Classifying a package as evidence and then never parsing it is the worst kind of gap.
        Assert.True(OracleSourceFile.IsSqlText(path));

    [Theory]
    [InlineData("forms/xml-exports/HRMS_EMPLOYEE.xml")]
    [InlineData("java-target/pom.xml")]
    [InlineData("docs/architecture.md")]
    public void Non_sql_files_are_not_read_as_sql(string path) =>
        Assert.False(OracleSourceFile.IsSqlText(path));
}
