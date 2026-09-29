using System.Security.Cryptography;
using System.Text;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Fleet.Execution.Adapters;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// The join between the GUI's prepare-sources operation and the normalization phase.
///
/// The positive case is the one the product needs: a binary module that nobody can open in this process
/// becomes a readable module in the intermediate representation because the source gateway opened it and
/// this server admitted the result. Every other case here is a forgery, a tampered file, or an
/// interrupted commit, and each of them has to stop the whole phase rather than quietly normalize the
/// part of the estate it still believes.
/// </summary>
public class PreparedSourceNormalizationTests
{
    private const string Operator = "migration-operator@contoso.com";
    private const string SourceRoot = "legacy/forms";
    private const string OuterRoot = "legacy";
    private const string Environment = "legacy-order-entry";
    private const string ModulePath = "legacy/forms/ui/ORDER_ENTRY.fmb";
    private const string Alias = "ORDER_ENTRY.fmb";
    private const string IrPath = "out/orders/intermediate/forms-ir.json";
    private const string ManifestPath = "out/orders/intermediate/forms-normalization-manifest.json";
    private const string WorkspaceOwner = "tenant-a:operator-a/project-a";

    private static readonly string s_profileHash = new('a', 64);
    private static readonly string s_ownerBinding = PreparedSourceOwnerBinding.Derive(WorkspaceOwner);
    private static readonly DateTimeOffset s_prepared = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] s_moduleBytes = [0x0A, 0x46, 0x4F, 0x52, 0x4D, 0x00, 0xFF, 0xFE];

    private static MigrationRunRequest Request(string formsVersion = "6i") => new()
    {
        EngagementId = "ENG-PREP",
        ApplicationName = "ORDERS",
        RequestedMode = ExecutionMode.GenerateArtifacts,
        Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
        OracleFormsVersion = formsVersion,
        OracleDatabaseVersion = "9i",
        SourceRoot = SourceRoot,
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

    [Fact]
    public async Task An_admitted_extraction_becomes_a_module_the_intermediate_reader_accepts()
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request());

        Assert.Equal(PhaseExecutionState.Executed, outcome.State);
        Assert.True(workspace.Exists(IrPath));

        FormsIntermediateRead read = FormsIntermediateReader.Read(workspace.Read(IrPath), SourceRoot);
        Assert.Null(read.Error);

        FormsModule module = Assert.Single(read.Modules!);
        Assert.Equal("ORDER_ENTRY", module.Name);
        Assert.Equal("Order entry", module.Title);
        Assert.Equal(ModulePath, module.SourcePath);

        // Nothing was carried as a bare name: this module's behaviour is its triggers, and they are here.
        Assert.Empty(module.ProgramUnits);
        Assert.Empty(module.Lovs);

        // The structure came out of the existing parser, so the owner qualifier is stripped from the base
        // table and the prompt loses its punctuation exactly as a supplied export's would.
        FormsBlock block = Assert.Single(module.Blocks);
        Assert.Equal("BANK_ACCOUNT", block.BaseTable);
        Assert.Equal(10, block.RecordsDisplayed);

        FormsItem item = Assert.Single(block.Items);
        Assert.Equal("ACCOUNT_ID", item.ColumnName);
        Assert.Equal("Account", item.Prompt);
        Assert.Equal(12, item.MaxLength);
        Assert.True(item.Required);

        // Behaviour is retained as untrusted source text, not translated.
        FormsTrigger moduleTrigger = Assert.Single(module.Triggers);
        Assert.Equal("WHEN-NEW-FORM-INSTANCE", moduleTrigger.Name);
        Assert.Equal("BEGIN :GLOBAL.X := 1; END;", moduleTrigger.Body);

        FormsTrigger itemTrigger = Assert.Single(block.Triggers);
        Assert.Equal("ORDER_BLOCK.ACCOUNT_ID", itemTrigger.Scope);

        // The facts beside it are the extraction's own structure, named by the digest of the admitted bytes.
        Assert.NotNull(module.SourceFacts);
        Assert.Equal("FormModule", module.SourceFacts!.Facts[0].LocalName);
        Assert.Null(module.SourceFacts.WrapperDeclaredVersion);
    }

    [Fact]
    public void A_prepared_claim_is_not_readable_under_a_different_owner_binding()
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);

        PreparedSourceTrustRead read = PreparedSourceTrustStore.Read(
            workspace.Root,
            PreparedSourceOwnerBinding.Derive("tenant-a:operator-b/project-b"));

        Assert.Null(read.Ledger);
        Assert.Contains("different project, tenant, or principal", read.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_representation_claims_no_baseline_and_no_schema_from_an_extraction()
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);

        MigrationExecutionResult result = await ExecuteAsync(workspace, Request());
        PhaseOutcome outcome = result.Phases.Single(phase => phase.Phase == MigrationPhase.SourceNormalization);

        Assert.Equal(PhaseExecutionState.Executed, outcome.State);
        Assert.Contains(
            outcome.Findings,
            finding => finding.Contains("not a behavioural baseline", StringComparison.Ordinal)
                && finding.Contains("no baseline or schema evidence follows", StringComparison.Ordinal));
        Assert.Contains("no baseline or schema evidence follows", workspace.Read(IrPath), StringComparison.Ordinal);
        Assert.Contains("grants no baseline or schema evidence", workspace.Read(ManifestPath), StringComparison.Ordinal);

        // A module finally becoming readable is not a reason for this phase to start signing anything.
        Assert.Empty(result.Attestations);
    }

    [Fact]
    public async Task An_uploaded_artifact_this_server_never_prepared_is_refused()
    {
        using TemporaryWorkspace workspace = Estate();

        // Exactly what a hostile archive carries: both documents, well formed, correlated with each other,
        // and with no server-side record of anyone having prepared them.
        workspace.WriteBytes(ArtifactPath, Extraction());
        workspace.WriteBytes(ProvenancePath, Provenance());

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request());

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("holds no record of preparing them", outcome.Detail!, StringComparison.Ordinal);
        Assert.Contains(ArtifactPath, outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task An_edited_artifact_no_longer_hashes_to_the_record_and_is_refused()
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);

        byte[] tampered = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(Extraction()).Replace("BANK_ACCOUNT", "PAYROLL", StringComparison.Ordinal));
        workspace.WriteBytes(ArtifactPath, tampered);

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request());

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("does not hash to the digest this server recorded", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task A_record_whose_artifact_never_landed_is_refused_rather_than_read_as_a_smaller_estate()
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);

        // The shape a commit that stopped between recording the claim and renaming the artifact leaves.
        File.Delete(workspace.Absolute(ArtifactPath));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request());

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("incomplete admission", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task An_extraction_pinned_to_a_module_the_estate_no_longer_holds_is_refused()
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);

        workspace.WriteBytes(ModulePath, [0x0A, 0x46, 0x4F, 0x52, 0x4D, 0x01]);

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request());

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("content digest it was pinned to", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task An_extraction_that_names_another_module_cannot_stand_in_for_this_one()
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace, moduleName: "PAYROLL_ENTRY");

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request());

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("filed under a different module's name", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task Extractions_prepared_against_two_profile_versions_stop_the_run()
    {
        using TemporaryWorkspace workspace = Estate();
        workspace.WriteBytes("legacy/forms/ui/INVOICES.fmb", [0x0A, 0x49, 0x4E, 0x56]);

        Prepare(workspace);
        Prepare(
            workspace,
            alias: "INVOICES.fmb",
            modulePath: "legacy/forms/ui/INVOICES.fmb",
            moduleName: "INVOICES",
            profileVersion: 9);

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request());

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("two different source environments", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task A_profile_release_that_contradicts_the_run_stops_it()
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace, release: "11g");

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request("6i"));

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("source environment profile", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task A_module_nobody_prepared_beside_one_that_was_still_fails_closed()
    {
        using TemporaryWorkspace workspace = Estate();
        workspace.WriteBytes("legacy/forms/ui/INVOICES.fmb", [0x0A, 0x49, 0x4E, 0x56]);
        Prepare(workspace);

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request());

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("INVOICES.fmb", outcome.Detail!, StringComparison.Ordinal);
        Assert.Contains("not covered one-to-one", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task A_menu_module_is_not_covered_by_an_extraction_of_a_form()
    {
        using TemporaryWorkspace workspace = Estate();
        workspace.WriteBytes("legacy/forms/ui/ORDER_ENTRY.mmb", [0x0A, 0x4D, 0x45, 0x4E, 0x55]);
        Prepare(workspace);

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request());

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("ORDER_ENTRY.mmb", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task An_artifact_claiming_adjudication_is_refused_even_when_this_server_admitted_it()
    {
        using TemporaryWorkspace workspace = Estate();

        // The record and the bytes agree, so only the document's own content is under test here: the
        // fields that say "this estate was normalized" belong to the phase running now and to nothing else.
        byte[] artifact = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(Extraction()).Replace("\"modules\":", "\"normalized\":true,\"modules\":", StringComparison.Ordinal));

        Prepare(workspace, artifact: artifact);

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request());

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("only this phase may write", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    /// <summary>
    /// An artifact a worker predating the fail-closed read could produce: the objects are named and what
    /// they do is absent. The record over it is genuine, so only the document's own content is under test.
    /// </summary>
    [Theory]
    [InlineData("[\"CALC_TOTAL\"]", "[]")]
    [InlineData("[]", "[\"ACCOUNT_LOV\"]")]
    public async Task A_prepared_extraction_naming_program_units_or_lovs_without_their_definitions_is_refused(
        string programUnits,
        string lovs)
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(
            workspace,
            artifact: Extraction(contentSha256: Sha256(s_moduleBytes), programUnits: programUnits, lovs: lovs));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request());

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("as names only", outcome.Detail!, StringComparison.Ordinal);
        Assert.Contains("cannot normalize behaviour it was never given", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task A_ledger_belonging_to_another_workspace_is_not_read_as_this_one()
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);

        string ledger = PreparedSourceTrustStore.LedgerPath(workspace.Root)!;
        File.WriteAllText(
            ledger,
            File.ReadAllText(ledger).Replace(
                Path.GetFileName(workspace.Root),
                new string('b', 32),
                StringComparison.Ordinal));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request());

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("belongs to the copy it was written for", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    /// <summary>
    /// The combined case the GUI produces: the operator prepared the modules and the database from the
    /// same source environment, so the copy carries two server-held ledgers and the prepared folder
    /// carries both sets of files. Reading only the module ledger used to refuse the statements as an
    /// upload and stop the phase.
    /// </summary>
    [Fact]
    public async Task A_prepared_schema_beside_a_prepared_module_normalizes_from_both_ledgers()
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);
        PrepareSchema(workspace);

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request());

        Assert.Equal(PhaseExecutionState.Executed, outcome.State);
        Assert.True(workspace.Exists(IrPath));

        Assert.True(workspace.Exists(SchemaDdlPath));
        Assert.True(workspace.Exists(SchemaProgramUnitPath));

        FormsIntermediateRead read = FormsIntermediateReader.Read(workspace.Read(IrPath), SourceRoot);
        Assert.Null(read.Error);

        FormsModule module = Assert.Single(read.Modules!);
        Assert.Equal("ORDER_ENTRY", module.Name);
        Assert.Equal(ModulePath, module.SourcePath);
    }

    [Fact]
    public async Task A_schema_this_server_never_prepared_is_refused()
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);

        workspace.WriteBytes(SchemaDdlPath, Encoding.UTF8.GetBytes("CREATE TABLE PUBLIC.T (ID NUMBER);\n"));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request());

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("holds no record of preparing them", outcome.Detail!, StringComparison.Ordinal);
        Assert.Contains(SchemaDdlPath, outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Edited_schema_statements_no_longer_hash_to_the_record_and_are_refused(bool programUnits)
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);
        PrepareSchema(workspace);

        workspace.WriteBytes(
            programUnits ? SchemaProgramUnitPath : SchemaDdlPath,
            Encoding.UTF8.GetBytes("GRANT ALL ON LEGACY.BANK_ACCOUNT TO PUBLIC;\n"));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request());

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("does not hash to the digest this server recorded", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task A_schema_record_whose_statements_never_landed_is_refused()
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);
        PrepareSchema(workspace);

        File.Delete(workspace.Absolute(SchemaDdlPath));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request());

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("incomplete admission", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task A_schema_prepared_under_another_owner_is_not_readable_by_this_run()
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);
        PrepareSchema(workspace, ownerBinding: PreparedSourceOwnerBinding.Derive("tenant-a:operator-b/project-b"));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request());

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("different project, tenant, or principal", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Theory]
    [InlineData("legacy-payroll", 4)]
    [InlineData(Environment, 9)]
    public async Task A_schema_prepared_against_another_source_environment_stops_the_run(string environment, int profileVersion)
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);
        PrepareSchema(workspace, environment: environment, profileVersion: profileVersion);

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request());

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("two different source environments", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task A_schema_whose_profile_hash_was_rewritten_stops_the_run()
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);
        PrepareSchema(workspace, profileHash: new string('c', 64));

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request());

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("two different source environments", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    /// <summary>
    /// A schema prepared on its own is admitted on its own. The run still fails, because the binary
    /// modules beside it were never prepared — but it fails for that reason, not because the statements
    /// read as an upload.
    /// </summary>
    [Fact]
    public async Task A_schema_prepared_without_any_module_is_still_admitted()
    {
        using TemporaryWorkspace workspace = Estate();
        PrepareSchema(workspace);

        PhaseOutcome outcome = await NormalizeAsync(workspace, Request());

        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.DoesNotContain("holds no record of preparing them", outcome.Detail!, StringComparison.Ordinal);
        Assert.Contains("binary Oracle Forms module(s)", outcome.Detail!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(IrPath));
    }

    [Fact]
    public async Task Database_conversion_refuses_statements_this_server_never_prepared()
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);

        workspace.WriteBytes(SchemaDdlPath, Encoding.UTF8.GetBytes("CREATE TABLE PUBLIC.BACKDOOR (ID NUMBER);\n"));

        PhaseExecutionResult result = await ConvertDatabaseAsync(workspace);

        Assert.False(result.Succeeded);
        Assert.Contains("holds no record of preparing them", result.FailureReason!, StringComparison.Ordinal);
        Assert.Contains(SchemaDdlPath, result.FailureReason!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(ConvertedSchemaPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Database_conversion_refuses_edited_prepared_statements(bool programUnits)
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);
        PrepareSchema(workspace);

        workspace.WriteBytes(
            programUnits ? SchemaProgramUnitPath : SchemaDdlPath,
            Encoding.UTF8.GetBytes("GRANT ALL ON LEGACY.BANK_ACCOUNT TO PUBLIC;\n"));

        PhaseExecutionResult result = await ConvertDatabaseAsync(workspace);

        Assert.False(result.Succeeded);
        Assert.Contains("does not hash to the digest this server recorded", result.FailureReason!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(ConvertedSchemaPath));
    }

    [Fact]
    public async Task Database_conversion_refuses_a_record_whose_statements_never_landed()
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);
        PrepareSchema(workspace);

        File.Delete(workspace.Absolute(SchemaDdlPath));

        PhaseExecutionResult result = await ConvertDatabaseAsync(workspace);

        Assert.False(result.Succeeded);
        Assert.Contains("incomplete admission", result.FailureReason!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(ConvertedSchemaPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Database_conversion_refuses_statements_prepared_under_another_owner(bool removeStatements)
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);
        PrepareSchema(workspace);
        if (removeStatements)
        {
            File.Delete(workspace.Absolute(SchemaDdlPath));
            File.Delete(workspace.Absolute(SchemaProgramUnitPath));
        }

        PhaseExecutionResult result = await ConvertDatabaseAsync(
            workspace,
            binding: PreparedSourceOwnerBinding.Derive("tenant-a:operator-b/project-b"));

        Assert.False(result.Succeeded);
        Assert.Contains("different project, tenant, or principal", result.FailureReason!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(ConvertedSchemaPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Database_conversion_refuses_when_the_schema_ledger_cannot_be_believed(bool removeStatements)
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);
        PrepareSchema(workspace);
        if (removeStatements)
        {
            File.Delete(workspace.Absolute(SchemaDdlPath));
            File.Delete(workspace.Absolute(SchemaProgramUnitPath));
        }

        File.WriteAllText(PreparedSchemaTrustStore.LedgerPath(workspace.Root)!, "{ not a document this server wrote");

        PhaseExecutionResult result = await ConvertDatabaseAsync(workspace);

        Assert.False(result.Succeeded);
        Assert.Contains("not a document this server wrote", result.FailureReason!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(ConvertedSchemaPath));
    }

    [Theory]
    [InlineData("legacy-payroll", 4, null)]
    [InlineData(Environment, 9, null)]
    [InlineData(Environment, 4, "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc")]
    public async Task Database_conversion_refuses_statements_from_another_source_environment(
        string environment,
        int profileVersion,
        string? profileHash)
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);
        PrepareSchema(workspace, environment: environment, profileVersion: profileVersion, profileHash: profileHash);

        PhaseExecutionResult result = await ConvertDatabaseAsync(workspace);

        Assert.False(result.Succeeded);
        Assert.Contains("two different source environments", result.FailureReason!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(ConvertedSchemaPath));
    }

    /// <summary>
    /// The combined input the GUI produces, converted by the phase that consumes it. Nothing normalized
    /// first: the admitted statements are readable on the strength of the server's own record alone.
    /// </summary>
    [Fact]
    public async Task Database_conversion_converts_an_admitted_schema()
    {
        using TemporaryWorkspace workspace = PreparedOnlyEstate();
        Prepare(workspace);
        PrepareSchema(workspace);

        PhaseExecutionResult result = await ConvertDatabaseAsync(workspace);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.True(workspace.Exists(ConvertedSchemaPath));
        Assert.Contains("bank_account", workspace.Read(ConvertedSchemaPath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// SQL an operator uploaded with their estate is ordinary source. It sits outside the folder only this
    /// server writes into, so it is not held to a preparation nobody made, and a copy that carries no
    /// prepared statements at all converts exactly as it did before the gate existed.
    /// </summary>
    [Fact]
    public async Task Database_conversion_still_converts_uploaded_sql_that_was_never_prepared()
    {
        using TemporaryWorkspace workspace = Estate();

        PhaseExecutionResult result = await ConvertDatabaseAsync(workspace);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.True(workspace.Exists(ConvertedSchemaPath));
    }

    [Fact]
    public async Task Application_conversion_refuses_statements_this_server_never_prepared()
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteBytes(SchemaDdlPath, Encoding.UTF8.GetBytes("CREATE TABLE PUBLIC.BACKDOOR (ID NUMBER);\n"));

        PhaseExecutionResult result = await ConvertApplicationAsync(workspace);

        Assert.False(result.Succeeded);
        Assert.Contains("holds no record of preparing them", result.FailureReason!, StringComparison.Ordinal);
        Assert.False(Directory.Exists(workspace.Absolute("out/orders/application")));
    }

    [Fact]
    public async Task Application_conversion_refuses_edited_prepared_statements()
    {
        using TemporaryWorkspace workspace = new();
        PrepareSchema(workspace);

        workspace.WriteBytes(SchemaDdlPath, Encoding.UTF8.GetBytes("CREATE TABLE LEGACY.BANK_ACCOUNT (ACCOUNT_ID NUMBER(12));\n"));

        PhaseExecutionResult result = await ConvertApplicationAsync(workspace);

        Assert.False(result.Succeeded);
        Assert.Contains("does not hash to the digest this server recorded", result.FailureReason!, StringComparison.Ordinal);
        Assert.False(Directory.Exists(workspace.Absolute("out/orders/application")));
    }

    [Fact]
    public async Task Application_conversion_generates_from_an_admitted_schema()
    {
        using TemporaryWorkspace workspace = new();
        PrepareSchema(workspace);

        PhaseExecutionResult result = await ConvertApplicationAsync(workspace);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.True(Directory.Exists(workspace.Absolute("out/orders/application")));
    }

    /// <summary>
    /// The highest-stakes consumer: these statements are executed against the target and the phase that
    /// runs them signs an attestation. Edited prepared INSERTs must not reach the gateway at all.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Data_phases_refuse_edited_prepared_statements_before_reaching_the_target(bool reconcile)
    {
        using TemporaryWorkspace workspace = Estate();
        PrepareSchema(workspace);

        workspace.WriteBytes(
            SchemaDdlPath,
            Encoding.UTF8.GetBytes("INSERT INTO BANK_ACCOUNT (ACCOUNT_ID) VALUES (99);\n"));

        UnreachableDataMigrationGateway gateway = new();

        PhaseExecutionResult result = await RunAsync(
            reconcile ? new DataReconciliationAdapter(gateway) : new SandboxDataMigrationAdapter(gateway),
            workspace,
            Request() with { RequestedMode = ExecutionMode.SandboxMigration, ExecutionApproval = Requests.Approved("release-manager@contoso.com") },
            binding: null);

        Assert.False(result.Succeeded);
        Assert.Contains("does not hash to the digest this server recorded", result.FailureReason!, StringComparison.Ordinal);
        Assert.False(gateway.Called);
    }

    /// <summary>
    /// Selecting a root above the one the source was prepared against. The reserved folder for the
    /// selected root is empty, so nothing here was ever claimed for it, while every consumer walks the
    /// whole tree and reads the nested statements as ordinary source.
    /// </summary>
    [Theory]
    [InlineData("database")]
    [InlineData("application")]
    [InlineData("sandbox")]
    [InlineData("reconciliation")]
    public async Task Every_consumer_refuses_prepared_statements_nested_below_the_selected_root(string consumer)
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/db/001_schema.sql", OracleSamples.Schema);
        workspace.WriteFile(
            $"{SourceRoot}/{PreparedSchemaTrustStore.PreparedFolder}/{Environment}{PreparedSchemaTrustStore.SchemaDdlSuffix}",
            "CREATE TABLE LEGACY.BACKDOOR (ACCOUNT_ID NUMBER(12));\n");

        PhaseExecutionResult result = await ConsumeAsync(consumer, workspace, Request() with { SourceRoot = OuterRoot });

        Assert.False(result.Succeeded);
        Assert.Contains("reserved prepared-source folder this run does not admit", result.FailureReason!, StringComparison.Ordinal);
        Assert.Contains($"{SourceRoot}/{PreparedSchemaTrustStore.PreparedFolder}", result.FailureReason!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(ConvertedSchemaPath));
    }

    /// <summary>
    /// Deleting every prepared statement, program units included, while the records over them stand. The
    /// copy then holds no prepared SQL at all, and concluding from that alone that there is nothing to
    /// check would convert the ordinary source beside it with outstanding admissions unaccounted for.
    /// </summary>
    [Theory]
    [InlineData("database")]
    [InlineData("application")]
    [InlineData("sandbox")]
    [InlineData("reconciliation")]
    public async Task Every_consumer_refuses_when_every_prepared_statement_was_deleted(string consumer)
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);
        PrepareSchema(workspace);

        File.Delete(workspace.Absolute(SchemaDdlPath));
        File.Delete(workspace.Absolute(SchemaProgramUnitPath));

        PhaseExecutionResult result = await ConsumeAsync(consumer, workspace, Request());

        Assert.False(result.Succeeded);
        Assert.Contains("incomplete admission", result.FailureReason!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(ConvertedSchemaPath));
    }

    /// <summary>
    /// The same nesting with the reserved folder deleted outright. Nothing is left on disk for the layout
    /// check to name, so the claims over the deleted statements are all that remains of them, and
    /// selecting the root above the one they were recorded against would filter them away and convert the
    /// ordinary SQL beside them as though the schema had never been prepared.
    /// </summary>
    [Theory]
    [InlineData("database")]
    [InlineData("application")]
    [InlineData("sandbox")]
    [InlineData("reconciliation")]
    public async Task Every_consumer_refuses_claims_recorded_below_the_selected_root_when_the_folder_was_deleted(string consumer)
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);
        PrepareSchema(workspace);

        Directory.Delete(workspace.Absolute($"{SourceRoot}/{PreparedSourceTrustStore.PreparedFolder}"), recursive: true);

        PhaseExecutionResult result = await ConsumeAsync(consumer, workspace, Request() with { SourceRoot = OuterRoot });

        Assert.False(result.Succeeded);
        Assert.Contains($"`{SourceRoot}/{PreparedSchemaTrustStore.PreparedFolder}`", result.FailureReason!, StringComparison.Ordinal);
        Assert.Contains(
            "Select the source root the statements were prepared against",
            result.FailureReason!,
            StringComparison.Ordinal);
        Assert.False(workspace.Exists(ConvertedSchemaPath));
    }

    /// <summary>
    /// The same deletion with a source root that reaches the prepared directory by another spelling. The
    /// file system resolves the alias, so every consumer still walks the tree the claims were recorded
    /// over, while the ordinal comparison that decides which claims this run answers for matches none of
    /// them and the outstanding admissions fall away.
    /// </summary>
    [Theory]
    [InlineData("database", "legacy/forms/.")]
    [InlineData("database", "./legacy/forms")]
    [InlineData("database", "legacy//forms")]
    [InlineData("application", "legacy/forms/.")]
    [InlineData("application", "./legacy/forms")]
    [InlineData("application", "legacy//forms")]
    [InlineData("sandbox", "legacy/forms/.")]
    [InlineData("sandbox", "./legacy/forms")]
    [InlineData("sandbox", "legacy//forms")]
    [InlineData("reconciliation", "legacy/forms/.")]
    [InlineData("reconciliation", "./legacy/forms")]
    [InlineData("reconciliation", "legacy//forms")]
    public async Task Every_consumer_refuses_a_selected_root_that_only_resolves_to_the_prepared_one(string consumer, string selected)
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);
        PrepareSchema(workspace);

        Directory.Delete(workspace.Absolute($"{SourceRoot}/{PreparedSourceTrustStore.PreparedFolder}"), recursive: true);

        PhaseExecutionResult result = await ConsumeAsync(consumer, workspace, Request() with { SourceRoot = selected });

        Assert.False(result.Succeeded);
        Assert.Contains("is not spelled the way a path in this workspace is spelled", result.FailureReason!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(ConvertedSchemaPath));
    }

    /// <summary>
    /// The spellings a directory answers to only on some file systems: a trailing dot or space, and a
    /// difference of case alone. Resolving them here would decide which root the operator selected, so
    /// each is refused by what it is written as rather than by what it happens to reach.
    /// </summary>
    [Theory]
    [InlineData("legacy/forms.", "is not spelled the way a path in this workspace is spelled")]
    [InlineData("legacy/forms ", "is not spelled the way a path in this workspace is spelled")]
    [InlineData("LEGACY/forms", "differs only in case")]
    [InlineData("LEGACY", "Select the source root")]
    [InlineData("legacy/.", "is not spelled the way a path in this workspace is spelled")]
    public void A_source_root_reaching_the_prepared_one_by_another_name_is_refused_rather_than_resolved(
        string selected,
        string expected)
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);
        PrepareSchema(workspace);

        Directory.Delete(workspace.Absolute($"{SourceRoot}/{PreparedSourceTrustStore.PreparedFolder}"), recursive: true);

        MigrationRunRequest request = Request();
        PhasePlan plan = MigrationRunPlanner.Plan(request).Phases.Single(phase => phase.Phase == MigrationPhase.DatabaseConversion);

        PhaseExecutionContext context = new(workspace.Root, selected, request.OutputRoot, plan, request, (_, _) => { })
        {
            PreparedSourceBinding = s_ownerBinding,
        };

        string? refusal = PreparedSchemaConsumption.Refusal(context, selected, "nothing was converted");

        Assert.NotNull(refusal);
        Assert.Contains(expected, refusal, StringComparison.Ordinal);
        Assert.Contains("nothing was converted", refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// The alias arriving from the record's side instead of the operator's. A claim naming a root this
    /// server would not have written reaches the same statements while comparing equal to no selection,
    /// so it is refused as an entry rather than compared as one.
    /// </summary>
    [Theory]
    [InlineData("LEGACY/forms", "differs only in case")]
    [InlineData("legacy/forms/.", "is not spelled the way this server writes one")]
    public async Task A_claim_recorded_against_a_root_this_server_would_not_have_written_is_refused(string claimRoot, string expected)
    {
        using TemporaryWorkspace workspace = Estate();
        PrepareSchema(workspace, claimRoot: claimRoot);

        PhaseExecutionResult result = await ConvertDatabaseAsync(workspace);

        Assert.False(result.Succeeded);
        Assert.Contains(expected, result.FailureReason!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(ConvertedSchemaPath));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("nested/")]
    public async Task A_claim_with_an_aliased_output_path_is_refused(string alias)
    {
        using TemporaryWorkspace workspace = Estate();
        PrepareSchema(workspace, ddlClaimPath: SchemaDdlPath.Replace("/extraction/", $"/extraction/{alias}/", StringComparison.Ordinal));

        PhaseExecutionResult result = await ConvertDatabaseAsync(workspace);

        Assert.False(result.Succeeded);
        Assert.Contains("is not spelled the way this server writes one", result.FailureReason!, StringComparison.Ordinal);
        Assert.False(workspace.Exists(ConvertedSchemaPath));
    }

    /// <summary>
    /// A claim recorded against a root the selected one does not contain. Those statements are outside the
    /// tree this run reads, so they are another selection's business and the selected root converts its
    /// own ordinary source.
    /// </summary>
    [Fact]
    public async Task A_claim_recorded_against_a_root_outside_the_selected_tree_leaves_it_convertible()
    {
        using TemporaryWorkspace workspace = Estate();
        Prepare(workspace);
        PrepareSchema(workspace);
        workspace.WriteFile("legacy/reports/db/001_schema.sql", OracleSamples.Schema);

        PhaseExecutionResult result = await RunAsync(
            new DatabaseConversionAdapter(),
            workspace,
            Request() with { SourceRoot = "legacy/reports" },
            binding: null);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.True(workspace.Exists(ConvertedSchemaPath));
    }

    /// <summary>
    /// The reserved name is a whole path segment, so a sibling that merely starts with it is an operator's
    /// own folder and the SQL in it is ordinary source that converts.
    /// </summary>
    [Fact]
    public async Task A_folder_whose_name_only_resembles_the_reserved_one_stays_ordinary_source()
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile($"{OuterRoot}/.fleet-source-archive/extraction/001_schema.sql", OracleSamples.Schema);
        workspace.WriteFile($"{SourceRoot}/.fleet-sourced/extraction/notes.sql", "CREATE TABLE LEGACY.NOTES (NOTE_ID NUMBER(12));\n");

        PhaseExecutionResult result = await RunAsync(
            new DatabaseConversionAdapter(),
            workspace,
            Request() with { SourceRoot = OuterRoot },
            binding: null);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.True(workspace.Exists(ConvertedSchemaPath));
    }

    /// <summary>
    /// A source root that names nothing. The tree the consumers would walk cannot be listed at all, so
    /// which reserved folders it holds is unknown, and unknown is refused rather than read as none.
    /// </summary>
    [Fact]
    public void An_empty_source_root_is_refused_rather_than_read_as_holding_no_prepared_folder()
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile(
            $"{SourceRoot}/{PreparedSchemaTrustStore.PreparedFolder}/{Environment}{PreparedSchemaTrustStore.SchemaDdlSuffix}",
            "CREATE TABLE LEGACY.BACKDOOR (ACCOUNT_ID NUMBER(12));\n");

        MigrationRunRequest request = Request();
        PhasePlan plan = MigrationRunPlanner.Plan(request).Phases.Single(phase => phase.Phase == MigrationPhase.DatabaseConversion);

        PhaseExecutionContext context = new(workspace.Root, string.Empty, request.OutputRoot, plan, request, (_, _) => { })
        {
            PreparedSourceBinding = s_ownerBinding,
        };

        string? refusal = PreparedSchemaConsumption.Refusal(context, string.Empty, "nothing was converted");

        Assert.NotNull(refusal);
        Assert.Contains(PreparedSchemaTrustStore.PreparedFolder, refusal, StringComparison.Ordinal);
        Assert.Contains("nothing was converted", refusal, StringComparison.Ordinal);
    }

    /// <summary>A gateway that fails the test if the phase reaches it.</summary>
    private sealed class UnreachableDataMigrationGateway : IDataMigrationGateway
    {
        public bool Called { get; private set; }

        public Task<SchemaDeploymentOutcome> PrepareAsync(IReadOnlyList<string> statements, CancellationToken cancellationToken) =>
            Reached<SchemaDeploymentOutcome>();

        public Task<IReadOnlyList<TableRowCount>> CountAsync(IReadOnlyList<string> tables, CancellationToken cancellationToken) =>
            Reached<IReadOnlyList<TableRowCount>>();

        public Task<IReadOnlyList<IReadOnlyList<string?>>> FetchAsync(string table, IReadOnlyList<string> columns, int maxRows, CancellationToken cancellationToken) =>
            Reached<IReadOnlyList<IReadOnlyList<string?>>>();

        public Task<DataMigrationOutcome> ApplyAsync(IReadOnlyList<DataMigrationStatement> statements, IReadOnlyList<string> tables, CancellationToken cancellationToken) =>
            Reached<DataMigrationOutcome>();

        private Task<T> Reached<T>()
        {
            Called = true;
            return Task.FromException<T>(new InvalidOperationException("The phase reached the target with statements it had not vouched for."));
        }
    }

    // ---------- fixtures ----------

    private static string ArtifactPath =>
        $"{SourceRoot}/{PreparedSourceTrustStore.PreparedFolder}/{Alias}{PreparedSourceTrustStore.ArtifactSuffix}";

    private static string ProvenancePath =>
        $"{SourceRoot}/{PreparedSourceTrustStore.PreparedFolder}/{Alias}{PreparedSourceTrustStore.ProvenanceSuffix}";

    private static string SchemaDdlPath =>
        $"{SourceRoot}/{PreparedSchemaTrustStore.PreparedFolder}/{Environment}{PreparedSchemaTrustStore.SchemaDdlSuffix}";

    private static string SchemaProgramUnitPath =>
        $"{SourceRoot}/{PreparedSchemaTrustStore.PreparedFolder}/{Environment}{PreparedSchemaTrustStore.ProgramUnitSuffix}";

    private const string ConvertedSchemaPath = "out/orders/database/postgresql/schema/schema.sql";

    private static TemporaryWorkspace Estate()
    {
        TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/001_schema.sql", OracleSamples.Schema);
        workspace.WriteBytes(ModulePath, s_moduleBytes);
        return workspace;
    }

    /// <summary>An estate whose only SQL is the prepared statements, so a conversion of it converts those.</summary>
    private static TemporaryWorkspace PreparedOnlyEstate()
    {
        TemporaryWorkspace workspace = new();
        workspace.WriteBytes(ModulePath, s_moduleBytes);
        return workspace;
    }

    /// <summary>
    /// Writes an admitted extraction the way the preparation service does: both documents into the source
    /// copy, and the claim over them into the server-held ledger outside it.
    /// </summary>
    private static void Prepare(
        TemporaryWorkspace workspace,
        string alias = Alias,
        string modulePath = ModulePath,
        string moduleName = "ORDER_ENTRY",
        int profileVersion = 4,
        string release = "6i",
        byte[]? artifact = null)
    {
        byte[] bytes = artifact ?? Extraction(alias, moduleName, profileVersion, Digest(workspace, modulePath));
        byte[] provenance = Provenance();

        string artifactPath = $"{SourceRoot}/{PreparedSourceTrustStore.PreparedFolder}/{alias}{PreparedSourceTrustStore.ArtifactSuffix}";
        string provenancePath = $"{SourceRoot}/{PreparedSourceTrustStore.PreparedFolder}/{alias}{PreparedSourceTrustStore.ProvenanceSuffix}";

        workspace.WriteBytes(artifactPath, bytes);
        workspace.WriteBytes(provenancePath, provenance);

        Assert.True(
            PreparedSourceTrustStore.TryAppend(
                workspace.Root,
                s_ownerBinding,
                new PreparedSourceClaim(
                    SourceRoot,
                    alias,
                    moduleName,
                    Digest(workspace, modulePath),
                    artifactPath,
                    Sha256(bytes),
                    bytes.Length,
                    provenancePath,
                    Sha256(provenance),
                    provenance.Length,
                    Environment,
                    profileVersion,
                    s_profileHash,
                    release,
                    "Stub source gateway for offline tests.",
                    s_prepared),
                out string error),
            error);
    }

    /// <summary>
    /// Writes an admitted schema extraction the way the preparation service does: the statements and
    /// their provenance into the same prepared folder the module artifacts use, and the claim over them
    /// into the server-held schema ledger outside the copy.
    /// </summary>
    private static void PrepareSchema(
        TemporaryWorkspace workspace,
        string environment = Environment,
        int profileVersion = 4,
        string? profileHash = null,
        string? ownerBinding = null,
        string? claimRoot = null,
        string? ddlClaimPath = null)
    {
        string hash = profileHash ?? s_profileHash;
        byte[] artifact = Encoding.UTF8.GetBytes(
            $$"""{"record":"fleet.source-schema-extract/1","sourceEnvironmentId":"{{environment}}"}""");
        byte[] ddl = Encoding.UTF8.GetBytes("CREATE TABLE LEGACY.BANK_ACCOUNT (ACCOUNT_ID NUMBER(12) NOT NULL);\n");
        byte[] programUnits = Encoding.UTF8.GetBytes("CREATE OR REPLACE PROCEDURE LEGACY.POST_TXN IS BEGIN NULL; END;\n");
        byte[] provenance = Encoding.UTF8.GetBytes(
            $$"""{"record":"fleet.source-schema-preparation/1","sourceEnvironmentId":"{{environment}}","profileHash":"{{hash}}"}""");

        string folder = $"{SourceRoot}/{PreparedSchemaTrustStore.PreparedFolder}";
        string ddlPath = $"{folder}/{environment}{PreparedSchemaTrustStore.SchemaDdlSuffix}";
        string programUnitPath = $"{folder}/{environment}{PreparedSchemaTrustStore.ProgramUnitSuffix}";
        string provenancePath = $"{folder}/{environment}{PreparedSchemaTrustStore.ProvenanceSuffix}";

        workspace.WriteBytes(ddlPath, ddl);
        workspace.WriteBytes(programUnitPath, programUnits);
        workspace.WriteBytes(provenancePath, provenance);

        Assert.True(
            PreparedSchemaTrustStore.TryAppend(
                workspace.Root,
                ownerBinding ?? s_ownerBinding,
                new PreparedSchemaClaim(
                    claimRoot ?? SourceRoot,
                    environment,
                    profileVersion,
                    hash,
                    ["LEGACY"],
                    Sha256(artifact),
                    artifact.Length,
                    ddlClaimPath ?? ddlPath,
                    Sha256(ddl),
                    programUnitPath,
                    Sha256(programUnits),
                    provenancePath,
                    Sha256(provenance),
                    1,
                    1,
                    1,
                    0,
                    1,
                    "Stub source gateway for offline tests.",
                    s_prepared),
                out string error),
            error);
    }

    private static byte[] Extraction(
        string alias = Alias,
        string moduleName = "ORDER_ENTRY",
        int profileVersion = 4,
        string? contentSha256 = null,
        string programUnits = "[]",
        string lovs = "[]") =>
        Encoding.UTF8.GetBytes($$"""

            {
              "generator": "{{SourceGatewayProtocol.ExtractedIrGenerator}}",
              "schemaVersion": "{{SourceGatewayProtocol.ExtractedIrSchemaVersion}}",
              "sourceEnvironmentId": "{{Environment}}",
              "profileVersion": {{profileVersion}},
              "profileHash": "{{s_profileHash}}",
              "moduleAlias": "{{alias}}",
              "contentSha256": "{{contentSha256 ?? Sha256(s_moduleBytes)}}",
              "formsFamily": "Forms6i",
              "versionAuthority": "source-worker-native",
              "modules": [
                {
                  "name": "{{moduleName}}",
                  "title": "Order entry",
                  "blocks": [
                    {
                      "name": "ORDER_BLOCK",
                      "baseTable": "LEGACY.BANK_ACCOUNT",
                      "recordsDisplayed": 10,
                      "items": [
                        {
                          "name": "ACCOUNT_ID",
                          "itemType": "Text Item",
                          "dataType": "Number",
                          "columnName": "ACCOUNT_ID",
                          "prompt": "Account:",
                          "required": true,
                          "visible": true,
                          "maxLength": 12
                        }
                      ],
                      "triggers": [
                        { "name": "WHEN-VALIDATE-ITEM", "scope": "ORDER_BLOCK.ACCOUNT_ID", "body": "BEGIN NULL; END;" }
                      ]
                    }
                  ],
                  "triggers": [
                    { "name": "WHEN-NEW-FORM-INSTANCE", "scope": "{{moduleName}}", "body": "BEGIN :GLOBAL.X := 1; END;" }
                  ],
                  "programUnits": {{programUnits}},
                  "lovs": {{lovs}}
                }
              ]
            }
            """);

    private static byte[] Provenance() => Encoding.UTF8.GetBytes(
        $$"""{"record":"fleet.source-preparation/1","normalized":false,"moduleAlias":"{{Alias}}","profileHash":"{{s_profileHash}}"}""");

    private static string Digest(TemporaryWorkspace workspace, string relativePath) =>
        Sha256(File.ReadAllBytes(workspace.Absolute(relativePath)));

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task<PhaseOutcome> NormalizeAsync(TemporaryWorkspace workspace, MigrationRunRequest request)
    {
        MigrationExecutionResult result = await ExecuteAsync(workspace, request);

        return result.Phases.Single(phase => phase.Phase == MigrationPhase.SourceNormalization);
    }

    private static Task<MigrationExecutionResult> ExecuteAsync(TemporaryWorkspace workspace, MigrationRunRequest request) =>
        new MigrationExecutor(
            workspace.Root,
            [new SourceNormalizationAdapter()],
            preparedSourceBinding: s_ownerBinding).ExecuteAsync(request, Operator);

    /// <summary>
    /// Runs one consuming adapter on its own, with no normalization outcome in the context, so what it
    /// reports is its own decision about the prepared statements and nothing it inherited.
    /// </summary>
    private static Task<PhaseExecutionResult> RunAsync(
        IPhaseAdapter adapter,
        TemporaryWorkspace workspace,
        MigrationRunRequest request,
        string? binding)
    {
        PhasePlan plan = MigrationRunPlanner.Plan(request).Phases.Single(phase => phase.Phase == adapter.Phase);

        return adapter.ExecuteAsync(
            new PhaseExecutionContext(workspace.Root, request.SourceRoot, request.OutputRoot, plan, request, (_, _) => { })
            {
                PreparedSourceBinding = binding ?? s_ownerBinding,
            },
            CancellationToken.None);
    }

    private static Task<PhaseExecutionResult> ConvertDatabaseAsync(TemporaryWorkspace workspace, string? binding = null) =>
        RunAsync(new DatabaseConversionAdapter(), workspace, Request(), binding);

    /// <summary>
    /// One named consumer of the prepared folder, so a check can be stated once for every phase that
    /// reads those bytes rather than for whichever one was written down first.
    /// </summary>
    private static Task<PhaseExecutionResult> ConsumeAsync(string consumer, TemporaryWorkspace workspace, MigrationRunRequest request) =>
        consumer switch
        {
            "database" => RunAsync(new DatabaseConversionAdapter(), workspace, request, binding: null),
            "application" => RunAsync(
                new ApplicationCodeConversionAdapter(),
                workspace,
                request with
                {
                    Evidence =
                    [
                        Requests.Evidence("EV-SCHEMA", EvidenceKind.DatabaseSchemaExport),
                        Requests.Evidence("EV-TEST", EvidenceKind.TestBaseline),
                    ],
                },
                binding: null),
            "sandbox" => RunAsync(
                new SandboxDataMigrationAdapter(new UnreachableDataMigrationGateway()),
                workspace,
                Mutating(request),
                binding: null),
            _ => RunAsync(
                new DataReconciliationAdapter(new UnreachableDataMigrationGateway()),
                workspace,
                Mutating(request),
                binding: null),
        };

    private static MigrationRunRequest Mutating(MigrationRunRequest request) =>
        request with
        {
            RequestedMode = ExecutionMode.SandboxMigration,
            ExecutionApproval = Requests.Approved("release-manager@contoso.com"),
        };

    /// <summary>
    /// The application conversion is driven from a request declaring no Forms evidence over an estate
    /// holding no Forms module, so the phase's own normalization prerequisite does not apply and the only
    /// thing that can refuse the run is the prepared-statement check under test.
    /// </summary>
    private static Task<PhaseExecutionResult> ConvertApplicationAsync(TemporaryWorkspace workspace, string? binding = null) =>
        RunAsync(
            new ApplicationCodeConversionAdapter(),
            workspace,
            Request() with
            {
                Evidence =
                [
                    Requests.Evidence("EV-SCHEMA", EvidenceKind.DatabaseSchemaExport),
                    Requests.Evidence("EV-TEST", EvidenceKind.TestBaseline),
                ],
            },
            binding);
}
