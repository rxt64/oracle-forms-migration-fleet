using System.Text;
using System.Text.Json;

namespace OracleFormsMigrationFleet.SourceWorker.Tests;

/// <summary>
/// The coverage contract: what the artifact is allowed to claim about a schema, and what the worker has to
/// refuse instead of claiming it.
///
/// The defect these exist for is the one an assertion about the DDL alone cannot see — an object kind no
/// read enumerated leaving a result that says <c>Extracted</c>. Every negative here therefore asserts that
/// no artifact was produced at all, not that a warning was attached to one.
///
/// Like the rest of this class of test, none of it is evidence that a real Oracle instance was reached.
/// </summary>
public sealed class SourceWorkerOracleCoverageTests
{
    private static readonly string[] s_meridian = ["MERIDIAN"];

    private static OracleSchemaExtractionService Service(FakeOracleDatabase database) =>
        new(new FakeOracleConnectionFactory(database, s_meridian));

    private static Task<OracleSchemaExtractionResult> Extract(FakeOracleDatabase database) =>
        Service(database).ExtractAsync(MeridianCatalog.Request(), default);

    private static JsonElement Artifact(OracleSchemaExtractionResult result) =>
        JsonDocument.Parse(Encoding.UTF8.GetString(result.SchemaArtifact!.Content)).RootElement.Clone();

    private static int Count(OracleSchemaExtractionResult result, string field) =>
        Artifact(result).GetProperty("coverage").GetProperty(field).GetInt32();

    private static string[] Objects(OracleSchemaExtractionResult result) =>
        [.. Artifact(result).GetProperty("objects").EnumerateArray()
            .Select(entry =>
                $"{entry.GetProperty("schema").GetString()}.{entry.GetProperty("name").GetString()} ({entry.GetProperty("kind").GetString()})")];

    private static async Task<OracleSchemaExtractionResult> Refused(FakeOracleDatabase database, string expected)
    {
        OracleSchemaExtractionResult result = await Extract(database);

        Assert.Equal("ExtractionFailed", result.Status);
        Assert.Null(result.SchemaArtifact);
        Assert.Null(result.SnapshotHash);
        Assert.Contains(result.Findings, finding => finding.Contains(expected, StringComparison.Ordinal));
        return result;
    }

    [Fact]
    public async Task The_artifact_reports_every_object_the_inventory_holds()
    {
        OracleSchemaExtractionResult result = await Extract(MeridianCatalog.Build());

        Assert.Equal("Extracted", result.Status);
        Assert.Equal(MeridianCatalog.ObjectCount, Count(result, "objects"));
        Assert.Equal(4, Count(result, "tables"));
        Assert.Equal(14, Count(result, "columns"));
        Assert.Equal(10, Count(result, "constraints"));
        Assert.Equal(5, Count(result, "indexes"));
        Assert.Equal(1, Count(result, "sequences"));
        Assert.Equal(0, Count(result, "grants"));
        Assert.Equal(1, Count(result, "programUnits"));
    }

    [Fact]
    public async Task The_artifact_names_every_object_it_covered()
    {
        OracleSchemaExtractionResult result = await Extract(MeridianCatalog.Build());

        Assert.Equal(
            [
                "MERIDIAN.CUSTOMERS (TABLE)",
                "MERIDIAN.ORDERS (TABLE)",
                "MERIDIAN.ORDER_ITEMS (TABLE)",
                "MERIDIAN.PRODUCTS (TABLE)",
                "MERIDIAN.PK_CUSTOMERS (INDEX)",
                "MERIDIAN.PK_ORDERS (INDEX)",
                "MERIDIAN.PK_ORDER_ITEMS (INDEX)",
                "MERIDIAN.PK_PRODUCTS (INDEX)",
                "MERIDIAN.UQ_PRODUCTS_NAME (INDEX)",
                "MERIDIAN.SEQ_ORDER_ID (SEQUENCE)",
                "MERIDIAN.PLACE_ORDER (PROCEDURE)",
            ],
            Objects(result));
    }

    [Fact]
    public async Task An_index_a_constraint_already_declares_is_not_written_twice()
    {
        string ddl = MeridianCatalog.Ddl(await Extract(MeridianCatalog.Build()));

        Assert.DoesNotContain("CREATE INDEX", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE UNIQUE INDEX", ddl, StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT \"PK_CUSTOMERS\" PRIMARY KEY (\"CUSTOMER_ID\")", ddl, StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT \"UQ_PRODUCTS_NAME\" UNIQUE (\"PRODUCT_NAME\")", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_unique_index_a_constraint_was_built_on_under_another_name_is_not_written_twice()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_OBJECTS", MeridianCatalog.Objects(["IX_PRODUCT_NAME", "INDEX", "VALID"]))
            .OverrideView("DBA_INDEXES", MeridianCatalog.Indexes(["IX_PRODUCT_NAME", "MERIDIAN", "PRODUCTS", "UNIQUE", "NORMAL"]))
            .OverrideView("DBA_IND_COLUMNS", MeridianCatalog.IndexColumns(["IX_PRODUCT_NAME", "PRODUCT_NAME", "1", "ASC"]));

        OracleSchemaExtractionResult result = await Extract(database);

        Assert.Equal("Extracted", result.Status);
        Assert.Equal(6, Count(result, "indexes"));
        Assert.DoesNotContain("CREATE UNIQUE INDEX", MeridianCatalog.Ddl(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_constraint_index_is_written_as_the_index_the_catalog_reported()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_OBJECTS", MeridianCatalog.Objects(["IDX_ORDERS_STATUS", "INDEX", "VALID"]))
            .OverrideView("DBA_INDEXES", MeridianCatalog.Indexes(["IDX_ORDERS_STATUS", "MERIDIAN", "ORDERS", "NONUNIQUE", "NORMAL"]))
            .OverrideView("DBA_IND_COLUMNS", MeridianCatalog.IndexColumns(
                ["IDX_ORDERS_STATUS", "STATUS", "1", "ASC"],
                ["IDX_ORDERS_STATUS", "ORDER_DATE", "2", "DESC"]));

        OracleSchemaExtractionResult result = await Extract(database);
        string ddl = MeridianCatalog.Ddl(result);

        Assert.Equal("Extracted", result.Status);
        Assert.Equal(6, Count(result, "indexes"));
        Assert.Contains(
            "CREATE INDEX \"MERIDIAN\".\"IDX_ORDERS_STATUS\" ON \"MERIDIAN\".\"ORDERS\" (\"STATUS\", \"ORDER_DATE\" DESC);",
            ddl,
            StringComparison.Ordinal);
        Assert.Contains("MERIDIAN.IDX_ORDERS_STATUS (INDEX)", Objects(result));
    }

    [Fact]
    public async Task A_unique_non_constraint_index_keeps_its_uniqueness()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_OBJECTS", MeridianCatalog.Objects(["UX_CUSTOMER_NAME", "INDEX", "VALID"]))
            .OverrideView("DBA_INDEXES", MeridianCatalog.Indexes(["UX_CUSTOMER_NAME", "MERIDIAN", "CUSTOMERS", "UNIQUE", "NORMAL"]))
            .OverrideView("DBA_IND_COLUMNS", MeridianCatalog.IndexColumns(["UX_CUSTOMER_NAME", "CUSTOMER_NAME", "1", "ASC"]));

        Assert.Contains(
            "CREATE UNIQUE INDEX \"MERIDIAN\".\"UX_CUSTOMER_NAME\" ON \"MERIDIAN\".\"CUSTOMERS\" (\"CUSTOMER_NAME\");",
            MeridianCatalog.Ddl(await Extract(database)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The host splits the artifact at the first ALTER SESSION line to separate schema DDL from PL/SQL, so
    /// an index or a grant written after that line would be stored as a program unit.
    /// </summary>
    [Fact]
    public async Task Indexes_and_grants_are_written_before_the_program_unit_section()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_OBJECTS", MeridianCatalog.Objects(["IDX_ORDERS_STATUS", "INDEX", "VALID"]))
            .OverrideView("DBA_INDEXES", MeridianCatalog.Indexes(["IDX_ORDERS_STATUS", "MERIDIAN", "ORDERS", "NONUNIQUE", "NORMAL"]))
            .OverrideView("DBA_IND_COLUMNS", MeridianCatalog.IndexColumns(["IDX_ORDERS_STATUS", "STATUS", "1", "ASC"]))
            .OverrideView("DBA_TAB_PRIVS", MeridianCatalog.ObjectPrivileges(
                ["MERIDIAN", "ORDERS", "REPORTING", "SELECT", "NO"]));

        string ddl = MeridianCatalog.Ddl(await Extract(database));

        int lastConstraint = ddl.LastIndexOf("ALTER TABLE", StringComparison.Ordinal);
        int index = ddl.IndexOf("CREATE INDEX", StringComparison.Ordinal);
        int grant = ddl.IndexOf("GRANT ", StringComparison.Ordinal);
        int section = ddl.IndexOf("ALTER SESSION SET CURRENT_SCHEMA = ", StringComparison.Ordinal);

        Assert.True(lastConstraint < index, "indexes follow the constraints");
        Assert.True(index < grant, "grants follow the indexes");
        Assert.True(grant < section, "grants precede the program-unit section");
    }

    [Fact]
    public async Task An_object_grant_is_written_back_with_its_grant_option()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_TAB_PRIVS", MeridianCatalog.ObjectPrivileges(
                ["MERIDIAN", "ORDERS", "REPORTING", "SELECT", "YES"],
                ["MERIDIAN", "ORDERS", "PUBLIC", "SELECT", "NO"],
                ["MERIDIAN", "PLACE_ORDER", "REPORTING", "EXECUTE", "NO"],
                ["MERIDIAN", "SEQ_ORDER_ID", "REPORTING", "SELECT", "NO"]));

        OracleSchemaExtractionResult result = await Extract(database);
        string ddl = MeridianCatalog.Ddl(result);

        Assert.Equal("Extracted", result.Status);
        Assert.Equal(4, Count(result, "grants"));
        Assert.Contains("GRANT SELECT ON \"MERIDIAN\".\"ORDERS\" TO \"REPORTING\" WITH GRANT OPTION;", ddl, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT ON \"MERIDIAN\".\"ORDERS\" TO PUBLIC;", ddl, StringComparison.Ordinal);
        Assert.Contains("GRANT EXECUTE ON \"MERIDIAN\".\"PLACE_ORDER\" TO \"REPORTING\";", ddl, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT ON \"MERIDIAN\".\"SEQ_ORDER_ID\" TO \"REPORTING\";", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_privilege_from_two_grantors_is_one_grant()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_TAB_PRIVS", MeridianCatalog.ObjectPrivileges(
                ["MERIDIAN", "ORDERS", "REPORTING", "SELECT", "NO"],
                ["MERIDIAN", "ORDERS", "REPORTING", "SELECT", "NO"]));

        OracleSchemaExtractionResult result = await Extract(database);

        Assert.Equal(1, Count(result, "grants"));
    }

    [Fact]
    public async Task A_lob_segment_is_covered_by_the_table_that_declares_it()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_OBJECTS", MeridianCatalog.Objects(["SYS_LOB0000031415C00004$$", "LOB", "VALID"]))
            .OverrideView("DBA_TAB_COLUMNS", FakeResultSet.Of(
                "TABLE_NAME, COLUMN_NAME, COLUMN_ID, DATA_TYPE, DATA_LENGTH, CHAR_LENGTH, CHAR_USED, DATA_PRECISION, DATA_SCALE, NULLABLE, DATA_DEFAULT",
                ["CUSTOMERS", "CUSTOMER_ID", "1", "NUMBER", "22", null, null, "10", "0", "N", null],
                ["CUSTOMERS", "CUSTOMER_NAME", "2", "VARCHAR2", "100", "100", "B", null, null, "N", null],
                ["CUSTOMERS", "CREDIT_LIMIT", "3", "NUMBER", "22", null, null, "12", "2", "Y", "0"],
                ["CUSTOMERS", "NOTES", "4", "CLOB", "4000", null, null, null, null, "Y", null],
                ["ORDERS", "ORDER_ID", "1", "NUMBER", "22", null, null, "10", "0", "N", null],
                ["ORDERS", "CUSTOMER_ID", "2", "NUMBER", "22", null, null, "10", "0", "N", null],
                ["ORDERS", "ORDER_DATE", "3", "DATE", "7", null, null, null, null, "N", "SYSDATE"],
                ["ORDERS", "STATUS", "4", "VARCHAR2", "20", "20", "B", null, null, "N", "'NEW'"],
                ["ORDER_ITEMS", "ORDER_ID", "1", "NUMBER", "22", null, null, "10", "0", "N", null],
                ["ORDER_ITEMS", "PRODUCT_ID", "2", "NUMBER", "22", null, null, "10", "0", "N", null],
                ["ORDER_ITEMS", "QUANTITY", "3", "NUMBER", "22", null, null, "8", "0", "N", "1"],
                ["ORDER_ITEMS", "UNIT_PRICE", "4", "NUMBER", "22", null, null, "12", "2", "N", null],
                ["PRODUCTS", "PRODUCT_ID", "1", "NUMBER", "22", null, null, "10", "0", "N", null],
                ["PRODUCTS", "PRODUCT_NAME", "2", "VARCHAR2", "80", "80", "B", null, null, "N", null],
                ["PRODUCTS", "UNIT_PRICE", "3", "NUMBER", "22", null, null, "12", "2", "N", null]));

        OracleSchemaExtractionResult result = await Extract(database);

        Assert.Equal("Extracted", result.Status);
        Assert.Equal(MeridianCatalog.ObjectCount + 1, Count(result, "objects"));
        Assert.Contains("\"NOTES\" CLOB\n", MeridianCatalog.Ddl(result), StringComparison.Ordinal);
        Assert.DoesNotContain("SYS_LOB", string.Join('\n', Objects(result)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("VIEW", "V_OPEN_ORDERS")]
    [InlineData("SYNONYM", "ORDERS_SYN")]
    [InlineData("MATERIALIZED VIEW", "MV_ORDER_TOTALS")]
    [InlineData("TRIGGER", "TRG_ORDERS_AUDIT")]
    [InlineData("DATABASE LINK", "FINANCE.WORLD")]
    [InlineData("JAVA CLASS", "OrderRouter")]
    [InlineData("TABLE PARTITION", "ORDERS_2003")]
    [InlineData("CLUSTER", "ORDER_CLUSTER")]
    [InlineData("LIBRARY", "LIB_ROUTING")]
    [InlineData("OPERATOR", "OP_MATCHES")]
    public async Task An_object_kind_this_build_cannot_rebuild_produces_no_artifact(string objectType, string name)
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_OBJECTS", MeridianCatalog.Objects([name, objectType, "VALID"]));

        OracleSchemaExtractionResult result = await Refused(database, $"MERIDIAN.{name} ({objectType})");

        Assert.Contains(result.Findings, finding => finding.Contains("does not extract", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Findings, finding => finding.Contains("warning", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The defect QA found: a view sat in the allowlist and the result still reported success.</summary>
    [Fact]
    public async Task A_view_in_an_approved_schema_is_no_longer_silently_absent()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_OBJECTS", MeridianCatalog.Objects(["V_OPEN_ORDERS", "VIEW", "VALID"]));

        OracleSchemaExtractionResult result = await Extract(database);

        Assert.NotEqual("Extracted", result.Status);
        Assert.Null(result.SchemaArtifact);
        Assert.Contains(result.Findings, finding => finding.Contains("V_OPEN_ORDERS", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("INVALID")]
    [InlineData("N/A")]
    public async Task An_object_the_instance_does_not_report_as_valid_produces_no_artifact(string status)
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_OBJECTS", MeridianCatalog.Objects(["RECALC_TOTALS", "PROCEDURE", status]));

        await Refused(database, $"MERIDIAN.RECALC_TOTALS (PROCEDURE is {status})");
    }

    [Fact]
    public async Task An_object_the_inventory_lists_but_no_read_returned_produces_no_artifact()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_OBJECTS", MeridianCatalog.Objects(["ARCHIVED_ORDERS", "TABLE", "VALID"]));

        await Refused(database, "ARCHIVED_ORDERS");
    }

    [Fact]
    public async Task An_index_the_inventory_does_not_list_produces_no_artifact()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_INDEXES", MeridianCatalog.Indexes(["IDX_GHOST", "MERIDIAN", "ORDERS", "NONUNIQUE", "NORMAL"]))
            .OverrideView("DBA_IND_COLUMNS", MeridianCatalog.IndexColumns(["IDX_GHOST", "STATUS", "1", "ASC"]));

        await Refused(database, "IDX_GHOST");
    }

    [Fact]
    public async Task A_program_unit_the_inventory_lists_but_the_source_read_missed_produces_no_artifact()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_SOURCE", MeridianCatalog.Source(MeridianCatalog.ProcedureBody, "PROCEDURE", "SHADOW_PROC"));

        await Refused(database, "PLACE_ORDER");
    }

    [Theory]
    [InlineData("BITMAP")]
    [InlineData("FUNCTION-BASED NORMAL")]
    [InlineData("DOMAIN")]
    [InlineData("IOT - TOP")]
    public async Task An_index_this_build_cannot_rebuild_produces_no_artifact(string indexType)
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_OBJECTS", MeridianCatalog.Objects(["IDX_ORDERS_STATUS", "INDEX", "VALID"]))
            .OverrideView("DBA_INDEXES", MeridianCatalog.Indexes(["IDX_ORDERS_STATUS", "MERIDIAN", "ORDERS", "NONUNIQUE", indexType]))
            .OverrideView("DBA_IND_COLUMNS", MeridianCatalog.IndexColumns(["IDX_ORDERS_STATUS", "STATUS", "1", "ASC"]));

        await Refused(database, indexType);
    }

    [Fact]
    public async Task An_index_with_no_columns_produces_no_artifact()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_OBJECTS", MeridianCatalog.Objects(["IDX_ORDERS_STATUS", "INDEX", "VALID"]))
            .OverrideView("DBA_INDEXES", MeridianCatalog.Indexes(["IDX_ORDERS_STATUS", "MERIDIAN", "ORDERS", "NONUNIQUE", "NORMAL"]));

        await Refused(database, "IDX_ORDERS_STATUS' returned no columns");
    }

    [Fact]
    public async Task An_index_on_a_table_outside_the_covered_set_produces_no_artifact()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_OBJECTS", MeridianCatalog.Objects(["IDX_LEDGER", "INDEX", "VALID"]))
            .OverrideView("DBA_INDEXES", MeridianCatalog.Indexes(["IDX_LEDGER", "FINANCE", "LEDGER", "NONUNIQUE", "NORMAL"]))
            .OverrideView("DBA_IND_COLUMNS", MeridianCatalog.IndexColumns(["IDX_LEDGER", "ENTRY_ID", "1", "ASC"]));

        await Refused(database, "FINANCE.LEDGER");
    }

    [Fact]
    public async Task A_column_level_privilege_produces_no_artifact()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_COL_PRIVS", MeridianCatalog.ColumnPrivileges(
                ["MERIDIAN", "CUSTOMERS", "CREDIT_LIMIT", "REPORTING", "UPDATE"]));

        await Refused(database, "UPDATE on MERIDIAN.CUSTOMERS.CREDIT_LIMIT to REPORTING");
    }

    [Fact]
    public async Task A_privilege_this_build_does_not_write_back_produces_no_artifact()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_TAB_PRIVS", MeridianCatalog.ObjectPrivileges(
                ["MERIDIAN", "ORDERS", "REPORTING", "ON COMMIT REFRESH", "NO"]));

        await Refused(database, "ON COMMIT REFRESH");
    }

    [Fact]
    public async Task A_grant_on_an_object_this_run_did_not_cover_produces_no_artifact()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_TAB_PRIVS", MeridianCatalog.ObjectPrivileges(
                ["MERIDIAN", "V_OPEN_ORDERS", "REPORTING", "SELECT", "NO"]));

        await Refused(database, "MERIDIAN.V_OPEN_ORDERS");
    }

    [Fact]
    public async Task A_same_schema_dependency_the_inventory_does_not_cover_produces_no_artifact()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_DEPENDENCIES", MeridianCatalog.Dependencies(["PLACE_ORDER", "MERIDIAN", "V_OPEN_ORDERS", null]));

        await Refused(database, "MERIDIAN.V_OPEN_ORDERS (absent from the inventory this run read)");
    }

    [Fact]
    public async Task A_server_owned_dependency_that_is_not_a_language_builtin_produces_no_artifact()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_DEPENDENCIES", MeridianCatalog.Dependencies(["PLACE_ORDER", "SYS", "DBMS_LOB", null]));

        await Refused(database, "SYS.DBMS_LOB (server-owned object this build does not cover)");
    }

    [Fact]
    public async Task A_dependency_the_catalog_did_not_name_produces_no_artifact()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_DEPENDENCIES", MeridianCatalog.Dependencies(["PLACE_ORDER", null, null, null]));

        await Refused(database, "a dependency the catalog did not name");
    }

    /// <summary>
    /// Every PL/SQL unit depends on the compiler's own packages whether or not its author wrote them, so
    /// the named builtins have to pass or nothing with a body would ever extract.
    /// </summary>
    [Fact]
    public async Task The_language_builtins_a_plsql_unit_compiles_against_do_not_block()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_DEPENDENCIES", MeridianCatalog.Dependencies(
                ["PLACE_ORDER", "SYS", "DUAL", null],
                ["PLACE_ORDER", "SYS", "DBMS_STANDARD", null],
                ["PLACE_ORDER", "SYS", "PLITBLM", null],
                ["PLACE_ORDER", "SYS", "SYS_STUB_FOR_PURITY_ANALYSIS", null]));

        Assert.Equal("Extracted", (await Extract(database)).Status);
    }

    [Fact]
    public async Task A_cross_schema_dependency_inside_the_allowlist_resolves_against_the_inventory()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_DEPENDENCIES", MeridianCatalog.Dependencies(["PLACE_ORDER", "PAYROLL", "STAFF", null]));

        OracleSchemaExtractionService service = new(new FakeOracleConnectionFactory(database, ["MERIDIAN", "PAYROLL"]));

        OracleSchemaExtractionResult result = await service.ExtractAsync(MeridianCatalog.Request("MERIDIAN", "PAYROLL"), default);

        Assert.Equal("ExtractionFailed", result.Status);
        Assert.Null(result.SchemaArtifact);
        Assert.Contains(result.Findings, finding => finding.Contains("PAYROLL.STAFF", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_inventory_read_binds_the_owner_like_every_other_read()
    {
        FakeOracleDatabase database = MeridianCatalog.Build();

        await Extract(database);

        FakeExecution inventory = database.Executed.First(execution => execution.CommandText.Contains("DBA_OBJECTS", StringComparison.Ordinal));

        Assert.Contains(":owner", inventory.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("MERIDIAN", inventory.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("MERIDIAN", inventory.Owner);
    }

    [Fact]
    public async Task A_refused_schema_still_reports_the_correlation_the_caller_sent()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_OBJECTS", MeridianCatalog.Objects(["V_OPEN_ORDERS", "VIEW", "VALID"]));

        OracleSchemaExtractionResult result = await Extract(database);

        Assert.Equal("meridian-9i", result.SourceEnvironmentId);
        Assert.Equal(3, result.ProfileVersion);
        Assert.Equal(new string('a', 64), result.ProfileHash);
        Assert.Equal(s_meridian, result.SchemaAllowlist);
        Assert.All(result.Capabilities, capability => Assert.Equal(CapabilityState.Rejected, capability.State));
    }

    [Fact]
    public void The_coverage_catalog_names_what_it_rebuilds_and_nothing_else()
    {
        Assert.Equal(
            ["FUNCTION", "INDEX", "PACKAGE", "PACKAGE BODY", "PROCEDURE", "SEQUENCE", "TABLE", "TYPE", "TYPE BODY"],
            OracleObjectCoverage.Emitted);
        Assert.Equal(["LOB"], OracleObjectCoverage.Derived);
        Assert.Equal(["NORMAL"], OracleObjectCoverage.IndexTypes);
        Assert.Equal(
            ["DBMS_STANDARD", "DUAL", "PLITBLM", "STANDARD", "SYS_STUB_FOR_PURITY_ANALYSIS"],
            OracleObjectCoverage.SystemBuiltins);

        Assert.All(
            new[] { "VIEW", "SYNONYM", "TRIGGER", "MATERIALIZED VIEW", "DATABASE LINK", "TABLE PARTITION" },
            kind => Assert.False(OracleObjectCoverage.IsCovered(kind)));
        Assert.All(
            new[] { "DBMS_LOB", "UTL_FILE", "DBMS_SQL", "UTL_HTTP" },
            name => Assert.False(OracleObjectCoverage.IsSystemBuiltin(name)));
    }
}
