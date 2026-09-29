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

    // ---------- fixtures ----------

    private static string ArtifactPath =>
        $"{SourceRoot}/{PreparedSourceTrustStore.PreparedFolder}/{Alias}{PreparedSourceTrustStore.ArtifactSuffix}";

    private static string ProvenancePath =>
        $"{SourceRoot}/{PreparedSourceTrustStore.PreparedFolder}/{Alias}{PreparedSourceTrustStore.ProvenanceSuffix}";

    private static TemporaryWorkspace Estate()
    {
        TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/001_schema.sql", OracleSamples.Schema);
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
}
