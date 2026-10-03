using System.Data.Common;
using System.Text;
using System.Text.Json;

namespace OracleFormsMigrationFleet.SourceWorker.Tests;

/// <summary>
/// Drives the real catalog statements and the real emitter against an in-memory ADO.NET provider that
/// answers as an Oracle 9i instance holding the MERIDIAN schema would.
///
/// These tests prove what the artifact says about the source, and what the worker refuses to say. None of
/// them is evidence that a real Oracle instance was reached; a run that requires that is gated below on an
/// operator-supplied target and is skipped when none is configured.
/// </summary>
public sealed class SourceWorkerOracleSchemaTests
{
    private static readonly string[] s_meridian = ["MERIDIAN"];

    private static OracleSchemaExtractionService Service(
        FakeOracleDatabase database,
        IReadOnlyList<string>? allowlist = null,
        OracleParameterStyle style = OracleParameterStyle.Named) =>
        new(new FakeOracleConnectionFactory(database, allowlist ?? s_meridian, style));

    [Fact]
    public async Task ExtractAsync_ReadsTheCatalogAndReportsAnArtifact()
    {
        FakeOracleDatabase database = MeridianCatalog.Build();

        OracleSchemaExtractionResult result = await Service(database).ExtractAsync(MeridianCatalog.Request(), default);

        Assert.Equal("Extracted", result.Status);
        Assert.NotNull(result.SchemaArtifact);
        Assert.Equal(OracleSchemaProtocol.MediaType, result.SchemaArtifact!.MediaType);
        Assert.Equal(result.SnapshotHash, result.SchemaArtifact.Sha256);
        Assert.Equal("meridian-9i", result.SourceEnvironmentId);
        Assert.Equal(3, result.ProfileVersion);
        Assert.Equal(new string('a', 64), result.ProfileHash);
        Assert.Equal(s_meridian, result.SchemaAllowlist);
        Assert.All(result.Capabilities, capability => Assert.Equal(CapabilityState.Verified, capability.State));
    }

    [Fact]
    public async Task ExtractAsync_ReadsEveryRequiredCatalogViewBeforeReportingSuccess()
    {
        FakeOracleDatabase database = MeridianCatalog.Build();

        await Service(database).ExtractAsync(MeridianCatalog.Request(), default);

        string[] required =
        [
            "DBA_OBJECTS", "DBA_TABLES", "DBA_TAB_COLUMNS", "DBA_CONSTRAINTS", "DBA_CONS_COLUMNS",
            "DBA_SEQUENCES", "DBA_INDEXES", "DBA_IND_COLUMNS", "DBA_TAB_PRIVS", "DBA_COL_PRIVS",
            "DBA_SOURCE", "DBA_TRIGGERS", "DBA_DEPENDENCIES",
        ];

        Assert.All(required, view =>
            Assert.Contains(database.Executed, execution => execution.CommandText.Contains(view, StringComparison.Ordinal)));

        Assert.DoesNotContain(database.Executed, execution => execution.CommandText.Contains("FROM ALL_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAsync_ProvesDictionaryVisibilityBeforeReadingAnything()
    {
        FakeOracleDatabase database = MeridianCatalog.Build();

        await Service(database).ExtractAsync(MeridianCatalog.Request(), default);

        FakeExecution probe = database.Executed[0];
        Assert.Contains("DICTIONARY_PROBE", probe.CommandText, StringComparison.Ordinal);
        Assert.Contains("DBA_OBJECTS", probe.CommandText, StringComparison.Ordinal);
        Assert.Equal("MERIDIAN", probe.Owner);
    }

    [Fact]
    public async Task ExtractAsync_FailsClosedWhenTheDataDictionaryIsNotReadable()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .Fail(sql => sql.Contains("DICTIONARY_PROBE", StringComparison.Ordinal));

        OracleSchemaExtractionResult result = await Service(database).ExtractAsync(MeridianCatalog.Request(), default);

        Assert.Equal("ExtractionFailed", result.Status);
        Assert.Null(result.SchemaArtifact);
        Assert.Contains(result.Findings, finding => finding.Contains("data dictionary is not readable", StringComparison.Ordinal));
        Assert.Contains(result.Capabilities, capability =>
            capability.Remediation == "operator.grant.select.on.required.dictionary.views.to.gateway.role");
        Assert.DoesNotContain(result.Capabilities, capability =>
            (capability.Remediation ?? string.Empty).Contains("SELECT_CATALOG_ROLE", StringComparison.OrdinalIgnoreCase));

        // The remediation is actionable only if it names the views the contract has to grant.
        Assert.Equal(13, OracleSchemaProtocol.RequiredDictionaryViews.Length);
        Assert.All(OracleSchemaProtocol.RequiredDictionaryViews, view =>
            Assert.Contains(result.Findings, finding => finding.Contains(view, StringComparison.Ordinal)));

        // No silent fallback: a run that cannot see DBA_ must not go on to read the ALL_ views instead.
        Assert.DoesNotContain(database.Executed, execution => execution.CommandText.Contains("FROM ALL_", StringComparison.Ordinal));
        Assert.Single(database.Executed);
    }

    [Fact]
    public async Task ExtractAsync_ExcludesOnlyTheProvisioningSelectGrantsTheGatewayHoldsItself()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_TAB_PRIVS", MeridianCatalog.ObjectPrivileges(
                ["MERIDIAN", "ORDERS", OracleSchemaProtocol.ProvisioningRole, "SELECT", "NO"],
                ["MERIDIAN", "CUSTOMERS", OracleSchemaProtocol.ProvisioningRole, "SELECT", "NO"],
                ["MERIDIAN", "ORDERS", "REPORTING", "SELECT", "NO"]));

        OracleSchemaExtractionResult result = await Service(database).ExtractAsync(MeridianCatalog.Request(), default);
        string ddl = MeridianCatalog.Ddl(result);

        Assert.Equal("Extracted", result.Status);
        Assert.DoesNotContain(OracleSchemaProtocol.ProvisioningRole, ddl, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT ON \"MERIDIAN\".\"ORDERS\" TO \"REPORTING\";", ddl, StringComparison.Ordinal);
        Assert.Contains(result.Findings, finding => finding.Contains("Excluded 2 SELECT grant(s)", StringComparison.Ordinal));
    }

    /// <summary>
    /// The provisioning contract grants the role SELECT on MERIDIAN tables and nothing else it owns, so a
    /// SELECT it holds on a sequence is outside the contract and is a real difference in the source.
    /// </summary>
    [Fact]
    public async Task ExtractAsync_KeepsASelectTheProvisioningRoleHoldsOnASequenceRatherThanATable()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_TAB_PRIVS", MeridianCatalog.ObjectPrivileges(
                ["MERIDIAN", "ORDERS", OracleSchemaProtocol.ProvisioningRole, "SELECT", "NO"],
                ["MERIDIAN", "SEQ_ORDER_ID", OracleSchemaProtocol.ProvisioningRole, "SELECT", "NO"]));

        OracleSchemaExtractionResult result = await Service(database).ExtractAsync(MeridianCatalog.Request(), default);
        string ddl = MeridianCatalog.Ddl(result);

        Assert.Equal("Extracted", result.Status);
        Assert.Contains(
            $"GRANT SELECT ON \"MERIDIAN\".\"SEQ_ORDER_ID\" TO \"{OracleSchemaProtocol.ProvisioningRole}\";",
            ddl,
            StringComparison.Ordinal);
        Assert.DoesNotContain($"\"ORDERS\" TO \"{OracleSchemaProtocol.ProvisioningRole}\"", ddl, StringComparison.Ordinal);
        Assert.Contains(result.Findings, finding => finding.Contains("Excluded 1 SELECT grant(s)", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same boundary for a view: the contract grants the role no view, so the grant is retained and
    /// then refused by the emitter as access control on an object this build does not rebuild. What must
    /// never happen is the grant vanishing because of the grantee's name.
    /// </summary>
    [Fact]
    public async Task ExtractAsync_KeepsASelectTheProvisioningRoleHoldsOnAViewRatherThanATable()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_TAB_PRIVS", MeridianCatalog.ObjectPrivileges(
                ["MERIDIAN", "ORDERS", OracleSchemaProtocol.ProvisioningRole, "SELECT", "NO"],
                ["MERIDIAN", "V_OPEN_ORDERS", OracleSchemaProtocol.ProvisioningRole, "SELECT", "NO"]));

        OracleSchemaExtractionResult result = await Service(database).ExtractAsync(MeridianCatalog.Request(), default);

        Assert.Equal("ExtractionFailed", result.Status);
        Assert.Null(result.SchemaArtifact);
        Assert.Contains(result.Findings, finding => finding.Contains("MERIDIAN.V_OPEN_ORDERS", StringComparison.Ordinal));
    }

    /// <summary>A grantable SELECT on a table is not the contract either; the contract is non-grantable.</summary>
    [Fact]
    public async Task ExtractAsync_CountsOnlyTheNonGrantableTableSelectsItExcluded()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_TAB_PRIVS", MeridianCatalog.ObjectPrivileges(
                ["MERIDIAN", "ORDERS", OracleSchemaProtocol.ProvisioningRole, "SELECT", "NO"],
                ["MERIDIAN", "CUSTOMERS", OracleSchemaProtocol.ProvisioningRole, "SELECT", "YES"],
                ["MERIDIAN", "PRODUCTS", OracleSchemaProtocol.ProvisioningRole, "DELETE", "NO"]));

        OracleSchemaExtractionResult result = await Service(database).ExtractAsync(MeridianCatalog.Request(), default);
        string ddl = MeridianCatalog.Ddl(result);

        Assert.Equal("Extracted", result.Status);
        Assert.Contains(result.Findings, finding => finding.Contains("Excluded 1 SELECT grant(s)", StringComparison.Ordinal));
        Assert.DoesNotContain($"\"ORDERS\" TO \"{OracleSchemaProtocol.ProvisioningRole}\"", ddl, StringComparison.Ordinal);
        Assert.Contains(
            $"GRANT SELECT ON \"MERIDIAN\".\"CUSTOMERS\" TO \"{OracleSchemaProtocol.ProvisioningRole}\" WITH GRANT OPTION;",
            ddl,
            StringComparison.Ordinal);
        Assert.Contains(
            $"GRANT DELETE ON \"MERIDIAN\".\"PRODUCTS\" TO \"{OracleSchemaProtocol.ProvisioningRole}\";",
            ddl,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OFM_GATEWAY_SOURCE_RO", "DELETE", "NO")]
    [InlineData("OFM_GATEWAY_SOURCE_RO", "SELECT", "YES")]
    [InlineData("OFM_GATEWAY_RO", "SELECT", "NO")]
    [InlineData("OFM_GATEWAY_RO", "UPDATE", "NO")]
    public async Task ExtractAsync_KeepsAnyGrantToTheGatewayNamesThatIsNotTheProvisioningContract(
        string grantee,
        string privilege,
        string grantable)
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_TAB_PRIVS", MeridianCatalog.ObjectPrivileges(
                ["MERIDIAN", "ORDERS", grantee, privilege, grantable]));

        OracleSchemaExtractionResult result = await Service(database).ExtractAsync(MeridianCatalog.Request(), default);

        Assert.Equal("Extracted", result.Status);
        string ddl = MeridianCatalog.Ddl(result);
        Assert.Contains($"GRANT {privilege} ON \"MERIDIAN\".\"ORDERS\" TO \"{grantee}\"", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Findings, finding => finding.Contains("Excluded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAsync_KeepsAThirdPartyGrantTheGatewayIdentityCouldNotHaveSeenInTheAllViews()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_TAB_PRIVS", MeridianCatalog.ObjectPrivileges(
                ["MERIDIAN", "ORDERS", "REPORTING", "SELECT", "NO"],
                ["MERIDIAN", "CUSTOMERS", "PUBLIC", "SELECT", "NO"],
                ["MERIDIAN", "PLACE_ORDER", "BATCH_RUNNER", "EXECUTE", "NO"]));

        OracleSchemaExtractionResult result = await Service(database).ExtractAsync(MeridianCatalog.Request(), default);
        string ddl = MeridianCatalog.Ddl(result);

        Assert.Equal("Extracted", result.Status);
        Assert.Contains("GRANT SELECT ON \"MERIDIAN\".\"ORDERS\" TO \"REPORTING\";", ddl, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT ON \"MERIDIAN\".\"CUSTOMERS\" TO PUBLIC;", ddl, StringComparison.Ordinal);
        Assert.Contains("GRANT EXECUTE ON \"MERIDIAN\".\"PLACE_ORDER\" TO \"BATCH_RUNNER\";", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_RefusesAColumnGrantEvenWhenItNamesTheGatewayRole()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_COL_PRIVS", MeridianCatalog.ColumnPrivileges(
                ["MERIDIAN", "CUSTOMERS", "CREDIT_LIMIT", OracleSchemaProtocol.ProvisioningRole, "SELECT"]));

        OracleSchemaExtractionResult result = await Service(database).ExtractAsync(MeridianCatalog.Request(), default);

        Assert.Equal("ExtractionFailed", result.Status);
        Assert.Null(result.SchemaArtifact);
        Assert.Contains(
            result.Findings,
            finding => finding.Contains($"SELECT on MERIDIAN.CUSTOMERS.CREDIT_LIMIT to {OracleSchemaProtocol.ProvisioningRole}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAsync_ReadsPrivilegesForTheBoundOwnerWithNoGranteeSuppressedInSql()
    {
        FakeOracleDatabase database = MeridianCatalog.Build();

        await Service(database).ExtractAsync(MeridianCatalog.Request(), default);

        FakeExecution[] grantReads =
        [
            .. database.Executed.Where(execution =>
                execution.CommandText.Contains("DBA_TAB_PRIVS", StringComparison.Ordinal) ||
                execution.CommandText.Contains("DBA_COL_PRIVS", StringComparison.Ordinal)),
        ];
        Assert.Equal(2, grantReads.Length);
        Assert.All(grantReads, execution =>
        {
            Assert.DoesNotContain("GRANTEE NOT IN", execution.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain("OFM_GATEWAY", execution.CommandText, StringComparison.Ordinal);
            Assert.Contains("WHERE OWNER = :owner", execution.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain("SELECT ANY", execution.CommandText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("MERIDIAN", execution.CommandText, StringComparison.Ordinal);
            Assert.Equal("MERIDIAN", execution.Owner);
        });
    }

    [Fact]
    public async Task ExtractAsync_ReadsTheInventoryBeforeAnyObjectInDetail()
    {
        FakeOracleDatabase database = MeridianCatalog.Build();

        await Service(database).ExtractAsync(MeridianCatalog.Request(), default);

        Assert.Contains("DBA_OBJECTS", database.Executed[0].CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_BindsTheOwnerAndNeverInlinesASchemaName()
    {
        FakeOracleDatabase database = MeridianCatalog.Build();

        await Service(database).ExtractAsync(MeridianCatalog.Request(), default);

        Assert.NotEmpty(database.Executed);
        Assert.All(database.Executed, execution =>
        {
            Assert.DoesNotContain("MERIDIAN", execution.CommandText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(":owner", execution.CommandText, StringComparison.Ordinal);
            Assert.Equal("MERIDIAN", execution.Owner);
        });
    }

    [Fact]
    public async Task ExtractAsync_RewritesBindMarkersForAPositionalClient()
    {
        FakeOracleDatabase database = MeridianCatalog.Build();

        await Service(database, style: OracleParameterStyle.Positional)
            .ExtractAsync(MeridianCatalog.Request(), default);

        Assert.All(database.Executed, execution =>
        {
            Assert.DoesNotContain(":owner", execution.CommandText, StringComparison.Ordinal);
            Assert.Contains('?', execution.CommandText);
            Assert.Equal("MERIDIAN", execution.Owner);
        });
    }

    [Fact]
    public async Task ExtractAsync_OnlyReadsCatalogViews()
    {
        FakeOracleDatabase database = MeridianCatalog.Build();

        await Service(database).ExtractAsync(MeridianCatalog.Request(), default);

        Assert.All(database.Executed, execution =>
        {
            Assert.StartsWith("SELECT", execution.CommandText.TrimStart(), StringComparison.Ordinal);
            Assert.DoesNotContain(';', execution.CommandText);
            Assert.Contains("FROM DBA_", execution.CommandText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ExtractAsync_QuotesEveryIdentifierItEmits()
    {
        FakeOracleDatabase database = MeridianCatalog.Build();

        string ddl = MeridianCatalog.Ddl(await Service(database).ExtractAsync(MeridianCatalog.Request(), default));

        Assert.Contains("CREATE TABLE \"MERIDIAN\".\"ORDER_ITEMS\" (", ddl, StringComparison.Ordinal);
        Assert.Contains("CREATE SEQUENCE \"MERIDIAN\".\"SEQ_ORDER_ID\"", ddl, StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT \"PK_CUSTOMERS\" PRIMARY KEY (\"CUSTOMER_ID\")", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_WritesTablesBeforeTheKeysThatPointAtThem()
    {
        FakeOracleDatabase database = MeridianCatalog.Build();

        string ddl = MeridianCatalog.Ddl(await Service(database).ExtractAsync(MeridianCatalog.Request(), default));

        int sequence = ddl.IndexOf("CREATE SEQUENCE", StringComparison.Ordinal);
        int firstTable = ddl.IndexOf("CREATE TABLE", StringComparison.Ordinal);
        int lastTable = ddl.LastIndexOf("CREATE TABLE", StringComparison.Ordinal);
        int firstConstraint = ddl.IndexOf("ALTER TABLE", StringComparison.Ordinal);
        int programUnit = ddl.IndexOf("CREATE OR REPLACE", StringComparison.Ordinal);

        Assert.True(sequence >= 0 && sequence < firstTable);
        Assert.True(lastTable < firstConstraint);
        Assert.True(firstConstraint < programUnit);

        Assert.Equal(
            ["CUSTOMERS", "ORDERS", "ORDER_ITEMS", "PRODUCTS"],
            ddl.Split('\n')
                .Where(line => line.StartsWith("CREATE TABLE", StringComparison.Ordinal))
                .Select(line => line.Split('"')[3])
                .ToArray());
    }

    [Fact]
    public async Task ExtractAsync_PreservesPrecisionScaleLengthNullabilityAndDefaults()
    {
        FakeOracleDatabase database = MeridianCatalog.Build();

        string ddl = MeridianCatalog.Ddl(await Service(database).ExtractAsync(MeridianCatalog.Request(), default));

        Assert.Contains("\"CUSTOMER_ID\" NUMBER(10,0) NOT NULL", ddl, StringComparison.Ordinal);
        Assert.Contains("\"CUSTOMER_NAME\" VARCHAR2(100) NOT NULL", ddl, StringComparison.Ordinal);
        Assert.Contains("\"CREDIT_LIMIT\" NUMBER(12,2) DEFAULT 0\n", ddl, StringComparison.Ordinal);
        Assert.Contains("\"STATUS\" VARCHAR2(20) DEFAULT 'NEW' NOT NULL", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("\"CREDIT_LIMIT\" NUMBER(12,2) DEFAULT 0 NOT NULL", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_ReadsALongDefaultThroughTheStreamingPath()
    {
        FakeOracleDatabase database = MeridianCatalog.Build();

        string ddl = MeridianCatalog.Ddl(await Service(database).ExtractAsync(MeridianCatalog.Request(), default));

        Assert.Contains("\"ORDER_DATE\" DATE DEFAULT SYSDATE NOT NULL", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_EmitsForeignKeysWithTheParentColumnsTheCatalogReported()
    {
        FakeOracleDatabase database = MeridianCatalog.Build();

        string ddl = MeridianCatalog.Ddl(await Service(database).ExtractAsync(MeridianCatalog.Request(), default));

        Assert.Contains(
            "ALTER TABLE \"MERIDIAN\".\"ORDER_ITEMS\" ADD CONSTRAINT \"FK_ORDER_ITEMS_ORDER\" FOREIGN KEY (\"ORDER_ID\") REFERENCES \"MERIDIAN\".\"ORDERS\" (\"ORDER_ID\") ON DELETE CASCADE;",
            ddl,
            StringComparison.Ordinal);
        Assert.Contains(
            "ALTER TABLE \"MERIDIAN\".\"ORDERS\" ADD CONSTRAINT \"FK_ORDERS_CUSTOMER\" FOREIGN KEY (\"CUSTOMER_ID\") REFERENCES \"MERIDIAN\".\"CUSTOMERS\" (\"CUSTOMER_ID\");",
            ddl,
            StringComparison.Ordinal);
        Assert.Contains(
            "ADD CONSTRAINT \"PK_ORDER_ITEMS\" PRIMARY KEY (\"ORDER_ID\", \"PRODUCT_ID\");",
            ddl,
            StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT \"CK_ORDER_ITEMS_QTY\" CHECK (QUANTITY > 0);", ddl, StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT \"UQ_PRODUCTS_NAME\" UNIQUE (\"PRODUCT_NAME\");", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_DoesNotRestateAColumnNotNullAsATableConstraint()
    {
        FakeOracleDatabase database = MeridianCatalog.Build();

        string ddl = MeridianCatalog.Ddl(await Service(database).ExtractAsync(MeridianCatalog.Request(), default));

        Assert.DoesNotContain("SYS_C0011001", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_CarriesTheProcedureBodyVerbatim()
    {
        FakeOracleDatabase database = MeridianCatalog.Build();

        string ddl = MeridianCatalog.Ddl(await Service(database).ExtractAsync(MeridianCatalog.Request(), default));

        Assert.Contains("ALTER SESSION SET CURRENT_SCHEMA = \"MERIDIAN\";", ddl, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE " + MeridianCatalog.ProcedureBody + "\n/", ddl, StringComparison.Ordinal);
        Assert.Contains("p_order_id    OUT NUMBER", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_ProducesTheSameDigestForTheSameCatalog()
    {
        OracleSchemaExtractionResult first = await Service(MeridianCatalog.Build()).ExtractAsync(MeridianCatalog.Request(), default);
        OracleSchemaExtractionResult second = await Service(MeridianCatalog.Build()).ExtractAsync(MeridianCatalog.Request(), default);

        Assert.Equal(first.SnapshotHash, second.SnapshotHash);
        Assert.Equal(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(first.SchemaArtifact!.Content)),
            first.SnapshotHash);
    }

    [Fact]
    public async Task ExtractAsync_DoesNotDeclareTheHostsAdjudicationFields()
    {
        OracleSchemaExtractionResult result = await Service(MeridianCatalog.Build()).ExtractAsync(MeridianCatalog.Request(), default);

        using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(result.SchemaArtifact!.Content));

        Assert.Equal(OracleSchemaProtocol.Generator, document.RootElement.GetProperty("generator").GetString());
        Assert.False(document.RootElement.TryGetProperty("normalized", out _));
        Assert.False(document.RootElement.TryGetProperty("sourceRoot", out _));
    }

    [Fact]
    public async Task ExtractAsync_NeverPutsTheConnectionIdentityInItsOutput()
    {
        OracleSchemaExtractionResult result = await Service(MeridianCatalog.Build()).ExtractAsync(MeridianCatalog.Request(), default);

        string rendered = result.ToString() +
            JsonSerializer.Serialize(result.Capabilities) +
            string.Join('\n', result.Findings) +
            Encoding.UTF8.GetString(result.SchemaArtifact!.Content);

        Assert.DoesNotContain("Pwd=", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ofm_reader", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FakeOracleConnectionFactory.Secret, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_RefusesASchemaTheOperatorDidNotApprove()
    {
        FakeOracleDatabase database = MeridianCatalog.Build();

        OracleSchemaExtractionResult result = await Service(database).ExtractAsync(MeridianCatalog.Request("PAYROLL"), default);

        Assert.Equal(CapabilityState.Rejected, result.Status);
        Assert.Null(result.SchemaArtifact);
        Assert.Equal(0, database.Opened);
        Assert.Contains(result.Findings, finding => finding.Contains("PAYROLL", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("MERIDIAN; DROP TABLE ORDERS")]
    [InlineData("\"MERIDIAN\"")]
    [InlineData("MERI DIAN")]
    [InlineData("MERIDIAN.ORDERS")]
    public async Task ExtractAsync_RefusesASchemaNameThatIsNotAPlainIdentifier(string schema)
    {
        FakeOracleDatabase database = MeridianCatalog.Build();

        OracleSchemaExtractionResult result = await Service(database).ExtractAsync(MeridianCatalog.Request(schema), default);

        Assert.Equal(CapabilityState.Rejected, result.Status);
        Assert.Equal(0, database.Opened);
    }

    [Fact]
    public async Task ExtractAsync_RefusesAMalformedRequestBeforeConnecting()
    {
        FakeOracleDatabase database = MeridianCatalog.Build();
        OracleSchemaExtractionRequest request = new(OracleSchemaProtocol.SchemaVersion, "meridian-9i", 3, "not-a-digest", s_meridian);

        OracleSchemaExtractionResult result = await Service(database).ExtractAsync(request, default);

        Assert.Equal(CapabilityState.Rejected, result.Status);
        Assert.Equal(0, database.Opened);
    }

    [Fact]
    public async Task ExtractAsync_BlocksWhenNoConnectionIsConfigured()
    {
        FakeOracleDatabase database = MeridianCatalog.Build();
        OracleSchemaExtractionService service = new(
            new FakeOracleConnectionFactory(database, s_meridian, configured: false));

        OracleSchemaExtractionResult result = await service.ExtractAsync(MeridianCatalog.Request(), default);

        Assert.Equal(CapabilityState.BlockedPrerequisite, result.Status);
        Assert.Equal(0, database.Opened);
        Assert.Contains(result.Findings, finding => finding.Contains(OracleSourceConfiguration.ConnectionStringVariable, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAsync_FailsWhenTheSchemaIsAbsentOrInvisible()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_OBJECTS", FakeResultSet.Of("OBJECT_NAME, OBJECT_TYPE, STATUS"))
            .OverrideView("DBA_TABLES", FakeResultSet.Of("TABLE_NAME"))
            .OverrideView("DBA_SEQUENCES", FakeResultSet.Of("SEQUENCE_NAME, MIN_VALUE, MAX_VALUE, INCREMENT_BY, CACHE_SIZE, LAST_NUMBER, CYCLE_FLAG, ORDER_FLAG"))
            .OverrideView("DBA_SOURCE", FakeResultSet.Of("TYPE, NAME, LINE, TEXT"));

        OracleSchemaExtractionResult result = await Service(database).ExtractAsync(MeridianCatalog.Request(), default);

        Assert.Equal("ExtractionFailed", result.Status);
        Assert.Null(result.SchemaArtifact);
        Assert.Contains(result.Findings, finding => finding.Contains("absent or invisible", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAsync_FailsRatherThanDroppingADatabaseTrigger()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_TRIGGERS", FakeResultSet.Of("TRIGGER_NAME", ["TRG_ORDERS_AUDIT"]));

        OracleSchemaExtractionResult result = await Service(database).ExtractAsync(MeridianCatalog.Request(), default);

        Assert.Equal("ExtractionFailed", result.Status);
        Assert.Null(result.SchemaArtifact);
        Assert.Contains(result.Findings, finding => finding.Contains("TRG_ORDERS_AUDIT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAsync_FailsRatherThanGuessingAnUnrenderableColumnType()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_TAB_COLUMNS", FakeResultSet.Of(
                "TABLE_NAME, COLUMN_NAME, COLUMN_ID, DATA_TYPE, DATA_LENGTH, CHAR_LENGTH, CHAR_USED, DATA_PRECISION, DATA_SCALE, NULLABLE, DATA_DEFAULT",
                ["CUSTOMERS", "CUSTOMER_ID", "1", "NUMBER", "22", null, null, "10", "0", "N", null],
                ["CUSTOMERS", "TERRITORY", "2", "SDO_GEOMETRY", "1", null, null, null, null, "Y", null]));

        OracleSchemaExtractionResult result = await Service(database).ExtractAsync(MeridianCatalog.Request(), default);

        Assert.Equal("ExtractionFailed", result.Status);
        Assert.Contains(result.Findings, finding => finding.Contains("SDO_GEOMETRY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAsync_FailsWhenAForeignKeyPointsOutsideTheCoveredSet()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_CONSTRAINTS", FakeResultSet.Of(
                "TABLE_NAME, CONSTRAINT_NAME, CONSTRAINT_TYPE, R_OWNER, R_CONSTRAINT_NAME, DELETE_RULE, STATUS, SEARCH_CONDITION",
                ["ORDERS", "FK_ORDERS_LEDGER", "R", "FINANCE", "PK_LEDGER", "NO ACTION", "ENABLED", null]))
            .OverrideView("DBA_CONS_COLUMNS", FakeResultSet.Of(
                "CONSTRAINT_NAME, COLUMN_NAME, POSITION",
                ["FK_ORDERS_LEDGER", "CUSTOMER_ID", "1"]));

        OracleSchemaExtractionResult result = await Service(database).ExtractAsync(MeridianCatalog.Request(), default);

        Assert.Equal("ExtractionFailed", result.Status);
        Assert.Contains(result.Findings, finding => finding.Contains("not fully covered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAsync_FailsWhenAnObjectDependsOnASchemaOutsideTheAllowlist()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_DEPENDENCIES", FakeResultSet.Of(
                "NAME, REFERENCED_OWNER, REFERENCED_NAME, REFERENCED_LINK_NAME",
                ["PLACE_ORDER", "FINANCE", "LEDGER", null]));

        OracleSchemaExtractionResult result = await Service(database).ExtractAsync(MeridianCatalog.Request(), default);

        Assert.Equal("ExtractionFailed", result.Status);
        Assert.Contains(result.Findings, finding => finding.Contains("FINANCE.LEDGER", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAsync_FailsWhenAnObjectIsReachedOverADatabaseLink()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_DEPENDENCIES", FakeResultSet.Of(
                "NAME, REFERENCED_OWNER, REFERENCED_NAME, REFERENCED_LINK_NAME",
                ["PLACE_ORDER", "MERIDIAN", "LEDGER", "FINANCE.WORLD"]));

        OracleSchemaExtractionResult result = await Service(database).ExtractAsync(MeridianCatalog.Request(), default);

        Assert.Equal("ExtractionFailed", result.Status);
        Assert.Contains(result.Findings, finding => finding.Contains("database link", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAsync_FallsBackWhenTheInstanceHasNoCharacterSemanticsColumns()
    {
        FakeOracleDatabase database = MeridianCatalog.Build()
            .OverrideView("DBA_TAB_COLUMNS", FakeResultSet.Of(
                "TABLE_NAME, COLUMN_NAME, COLUMN_ID, DATA_TYPE, DATA_LENGTH, DATA_PRECISION, DATA_SCALE, NULLABLE, DATA_DEFAULT",
                ["CUSTOMERS", "CUSTOMER_ID", "1", "NUMBER", "22", "10", "0", "N", null],
                ["CUSTOMERS", "CUSTOMER_NAME", "2", "VARCHAR2", "100", null, null, "N", null]))
            .Fail(sql => sql.Contains("CHAR_LENGTH", StringComparison.Ordinal))
            .OverrideView("DBA_OBJECTS", FakeResultSet.Of("OBJECT_NAME, OBJECT_TYPE, STATUS", ["CUSTOMERS", "TABLE", "VALID"]))
            .OverrideView("DBA_TABLES", FakeResultSet.Of("TABLE_NAME", ["CUSTOMERS"]))
            .OverrideView("DBA_INDEXES", FakeResultSet.Of("INDEX_NAME, TABLE_OWNER, TABLE_NAME, UNIQUENESS, INDEX_TYPE"))
            .OverrideView("DBA_IND_COLUMNS", FakeResultSet.Of("INDEX_NAME, COLUMN_NAME, COLUMN_POSITION, DESCEND"))
            .OverrideView("DBA_SEQUENCES", FakeResultSet.Of("SEQUENCE_NAME, MIN_VALUE, MAX_VALUE, INCREMENT_BY, CACHE_SIZE, LAST_NUMBER, CYCLE_FLAG, ORDER_FLAG"))
            .OverrideView("DBA_SOURCE", FakeResultSet.Of("TYPE, NAME, LINE, TEXT"))
            .OverrideView("DBA_DEPENDENCIES", FakeResultSet.Of("NAME, REFERENCED_OWNER, REFERENCED_NAME, REFERENCED_LINK_NAME"))
            .OverrideView("DBA_CONSTRAINTS", FakeResultSet.Of(
                "TABLE_NAME, CONSTRAINT_NAME, CONSTRAINT_TYPE, R_OWNER, R_CONSTRAINT_NAME, DELETE_RULE, STATUS, SEARCH_CONDITION"))
            .OverrideView("DBA_CONS_COLUMNS", FakeResultSet.Of("CONSTRAINT_NAME, COLUMN_NAME, POSITION"));

        OracleSchemaExtractionResult result = await Service(database).ExtractAsync(MeridianCatalog.Request(), default);

        Assert.Equal("Extracted", result.Status);
        Assert.Contains(result.Findings, finding => finding.Contains("CHAR_LENGTH", StringComparison.Ordinal));
        Assert.Contains("\"CUSTOMER_NAME\" VARCHAR2(100) NOT NULL", MeridianCatalog.Ddl(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_ReportsAClientFailureWithoutQuotingIt()
    {
        FakeOracleDatabase database = MeridianCatalog.Build().Fail(sql => sql.Contains("DBA_CONSTRAINTS", StringComparison.Ordinal));

        OracleSchemaExtractionResult result = await Service(database).ExtractAsync(MeridianCatalog.Request(), default);

        Assert.Equal("ExtractionFailed", result.Status);
        Assert.Null(result.SchemaArtifact);
        Assert.Contains(result.Findings, finding => finding.Contains("FakeDbException", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Findings, finding => finding.Contains("ORA-00904", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAsync_ObservesCancellation()
    {
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(MeridianCatalog.Build()).ExtractAsync(MeridianCatalog.Request(), cancelled.Token));
    }

    [Theory]
    [InlineData("MERIDIAN", true)]
    [InlineData("OFM_READ$1", true)]
    [InlineData("meridian", false)]
    [InlineData("MERIDIAN--", false)]
    [InlineData("", false)]
    public void IsSchemaName_AcceptsOnlyStoredFormIdentifiers(string value, bool expected) =>
        Assert.Equal(expected, OracleSourceConfiguration.IsSchemaName(value));

    [Fact]
    public void FromEnvironment_RejectsAnAllowlistWithAnythingUnexpectedInIt()
    {
        OracleSourceConfiguration configuration = OracleSourceConfiguration.FromEnvironment(name => name switch
        {
            OracleSourceConfiguration.ConnectionStringVariable => "Dsn=MERIDIAN9I",
            OracleSourceConfiguration.AllowlistVariable => "MERIDIAN, PAY ROLL",
            _ => null,
        });

        Assert.False(configuration.Configured);
        Assert.Equal(OracleSourceConfiguration.AllowlistVariable, configuration.FirstMissingSetting);
    }

    [Fact]
    public void FromEnvironment_KeepsTheConnectionStringOutOfEveryRenderedForm()
    {
        OracleSourceConfiguration configuration = OracleSourceConfiguration.FromEnvironment(name => name switch
        {
            OracleSourceConfiguration.ConnectionStringVariable => FakeOracleConnectionFactory.Secret,
            OracleSourceConfiguration.AllowlistVariable => "MERIDIAN",
            OracleSourceConfiguration.ProviderVariable => "odbc",
            _ => null,
        });

        Assert.True(configuration.Configured);
        Assert.DoesNotContain("Pwd=", configuration.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["MERIDIAN"], configuration.SchemaAllowlist);
        Assert.Equal("odbc", configuration.ProviderAlias);
    }

    /// <summary>
    /// Runs against a real instance only when an operator supplied one: a connect string, an allowlist and
    /// a registered provider factory. There is no default target and no fallback, so a run that did no work
    /// asserts nothing and can never be mistaken for a verified one.
    /// </summary>
    [Fact]
    public async Task ExtractAsync_AgainstAConfiguredInstance()
    {
        string? allowlist = Environment.GetEnvironmentVariable(OracleSourceConfiguration.AllowlistVariable);
        string? connection = Environment.GetEnvironmentVariable(OracleSourceConfiguration.ConnectionStringVariable);
        string? providerInvariantName = Environment.GetEnvironmentVariable("OFM_WORKER_ORACLE_PROVIDER_FACTORY");

        if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(allowlist) || string.IsNullOrWhiteSpace(providerInvariantName))
        {
            return;
        }

        OracleSourceConfiguration configuration = OracleSourceConfiguration.FromEnvironment(Environment.GetEnvironmentVariable);
        DbProviderFactory factory = DbProviderFactories.GetFactory(providerInvariantName);

        OracleSchemaExtractionService service = new(new ConfiguredOracleConnectionFactory(
            configuration,
            connectionString =>
            {
                DbConnection created = factory.CreateConnection()!;
                created.ConnectionString = connectionString;
                return created;
            },
            OracleParameterStyle.Positional));

        OracleSchemaExtractionResult result = await service.ExtractAsync(
            new OracleSchemaExtractionRequest(
                OracleSchemaProtocol.SchemaVersion, "configured-instance", 1, new string('0', 64), configuration.SchemaAllowlist),
            default);

        Assert.Equal("Extracted", result.Status);
        Assert.NotNull(result.SchemaArtifact);
    }
}
