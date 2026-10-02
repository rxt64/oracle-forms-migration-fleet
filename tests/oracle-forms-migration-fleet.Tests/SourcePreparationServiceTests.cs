// Copyright (c) Microsoft. All rights reserved.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// The GUI-reachable prepare-sources operation, end to end inside the host.
///
/// The gateway itself is a stub, because the thing under test is what this host authorizes, pins, admits
/// and keeps — not whether a Forms library loads. Nothing here claims a native capability, and the
/// default-unconfigured case is asserted first so a deployment without a gateway keeps refusing.
/// </summary>
public sealed class SourcePreparationServiceTests : IDisposable
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ofm-prepare-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Without_a_configured_gateway_the_operation_refuses_and_contacts_nothing()
    {
        await using Harness harness = await Harness.CreateAsync(_root, gateway: null);

        PlatformResult<SourcePreparationReport> result = await harness.PrepareAsync(["ORDERS.fmb"]);

        Assert.False(result.Succeeded);
        Assert.Equal(503, result.Status);
        Assert.Contains("No authorized source gateway is configured", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_extracted_module_is_stored_with_provenance_and_the_snapshot_is_refreshed()
    {
        StubGateway gateway = new();
        await using Harness harness = await Harness.CreateAsync(_root, gateway);
        string beforeHash = harness.SnapshotHash();

        PlatformResult<SourcePreparationReport> result = await harness.PrepareAsync(["ORDERS.fmb"]);

        Assert.True(result.Succeeded, result.Error);
        SourcePreparationReport report = result.Value!;
        Assert.Equal(1, report.ExtractedCount);
        Assert.True(report.SnapshotRefreshed);

        PreparedModuleReport module = Assert.Single(report.Modules);
        Assert.Equal(SourceGatewayStatus.Extracted, module.Status);
        Assert.Equal("ORDERS", module.ModuleIdentity);
        Assert.Equal("forms/.fleet-source/extraction/ORDERS.fmb.forms-ir.json", module.ArtifactPath);

        // The bytes on disk are the bytes that were hashed, and the digest reported is theirs.
        byte[] stored = File.ReadAllBytes(Path.Combine(
            harness.WorkspacePath, "forms", ".fleet-source", "extraction", "ORDERS.fmb.forms-ir.json"));
        Assert.Equal(module.ArtifactSha256, Convert.ToHexStringLower(SHA256.HashData(stored)));
        Assert.Equal(gateway.LastArtifact, stored);

        // Provenance records what the gateway observed and refuses to imply adjudication.
        using JsonDocument provenance = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            harness.WorkspacePath, "forms", ".fleet-source", "extraction", "ORDERS.fmb.provenance.json")));
        Assert.False(provenance.RootElement.GetProperty("normalized").GetBoolean());
        Assert.Equal(harness.ProfileHash, provenance.RootElement.GetProperty("profileHash").GetString());
        Assert.Equal(module.ContentSha256, provenance.RootElement.GetProperty("expectedContentSha256").GetString());

        // Writing into the copy changes its identity, so an authorization bound to the old one stops matching.
        Assert.NotEqual(beforeHash, harness.SnapshotHash());
    }

    [Fact]
    public async Task The_pinned_digest_is_the_one_this_host_computed_from_its_own_copy()
    {
        StubGateway gateway = new();
        await using Harness harness = await Harness.CreateAsync(_root, gateway);

        await harness.PrepareAsync(["ORDERS.fmb"]);

        SourceGatewayFormsModuleRequest sent = Assert.Single(gateway.Requests);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Harness.ModuleContent))),
            sent.ExpectedContentSha256);
        Assert.Equal("ORDERS.fmb", sent.ModuleAlias);
        Assert.Equal(harness.ProfileHash, sent.ProfileHash);
        Assert.Equal(SourceGatewayProtocol.SchemaVersion, sent.SchemaVersion);
    }

    [Fact]
    public async Task A_caller_from_another_project_cannot_prepare_this_project_source()
    {
        StubGateway gateway = new();
        await using Harness harness = await Harness.CreateAsync(_root, gateway);

        PlatformResult<SourcePreparationReport> result = await harness.PrepareAsync(
            ["ORDERS.fmb"], actor: Harness.Outsider);

        Assert.False(result.Succeeded);
        // A non-member is told the project was not found rather than that it exists and was refused.
        Assert.Equal(404, result.Status);
        Assert.Empty(gateway.Requests);
    }

    [Fact]
    public async Task A_member_who_does_not_own_the_copy_gets_nothing_about_it()
    {
        StubGateway gateway = new();
        await using Harness harness = await Harness.CreateAsync(_root, gateway);
        await harness.AddMemberAsync(Harness.SecondMember);

        PlatformResult<SourcePreparationReport> result = await harness.PrepareAsync(
            ["ORDERS.fmb"], actor: Harness.SecondMember);

        Assert.False(result.Succeeded);
        Assert.Equal(404, result.Status);
        Assert.Contains("does not belong to you", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(gateway.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_profile_change_while_remote_IO_is_in_flight_commits_no_files_or_trust_claims(bool schema)
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubGateway gateway = new() { Gate = gate };
        await using Harness harness = await Harness.CreateAsync(_root, gateway);

        Task<(int Status, string Detail)> pending = PrepareThroughGatewayAsync(harness, schema);
        await gateway.Entered.Task;
        await harness.RedeclareAsync(SourceConnector.OracleDatabaseReader, ["LEGACY_LAB"]);
        gate.SetResult();

        (int status, string detail) = await pending;

        Assert.Contains("source environment changed", detail, StringComparison.OrdinalIgnoreCase);
        AssertNoPreparedCommit(harness);
        Assert.True(status is 200 or 409);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Membership_revoked_while_remote_IO_is_in_flight_commits_no_files_or_trust_claims(bool schema)
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubGateway gateway = new() { Gate = gate };
        await using Harness harness = await Harness.CreateAsync(_root, gateway);

        Task<(int Status, string Detail)> pending = PrepareThroughGatewayAsync(harness, schema);
        await gateway.Entered.Task;
        await harness.RevokeOperatorAsync();
        gate.SetResult();

        (int status, string detail) = await pending;

        Assert.Contains("permission", detail, StringComparison.OrdinalIgnoreCase);
        AssertNoPreparedCommit(harness);
        Assert.True(status is 200 or 409);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Different_workspace_content_while_remote_IO_is_in_flight_commits_no_files_or_trust_claims(bool schema)
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubGateway gateway = new() { Gate = gate };
        await using Harness harness = await Harness.CreateAsync(_root, gateway);

        Task<(int Status, string Detail)> pending = PrepareThroughGatewayAsync(harness, schema);
        await gateway.Entered.Task;
        harness.ChangeSuppliedModuleBytes();
        gate.SetResult();

        (int status, string detail) = await pending;

        Assert.Contains("source copy changed", detail, StringComparison.OrdinalIgnoreCase);
        AssertNoPreparedCommit(harness);
        Assert.True(status is 200 or 409);
    }

    [Fact]
    public async Task Concurrent_distinct_preparations_retain_both_trust_claims()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubGateway gateway = new() { Gate = gate };
        await using Harness harness = await Harness.CreateAsync(_root, gateway);

        Task<PlatformResult<SourcePreparationReport>> orders = harness.PrepareAsync(["ORDERS.fmb"]);
        Task<PlatformResult<SourcePreparationReport>> invoices = harness.PrepareAsync(["INVOICES.fmb"]);
        await gateway.Entered.Task;
        Assert.Equal(2, gateway.Requests.Count);
        gate.SetResult();

        PlatformResult<SourcePreparationReport>[] results = await Task.WhenAll(orders, invoices);

        Assert.All(results, result => Assert.Equal(1, result.Value!.ExtractedCount));
        PreparedSourceTrustRead read = PreparedSourceTrustStore.Read(harness.WorkspacePath, harness.OwnerBinding);
        Assert.Null(read.Error);
        Assert.Equal(
            ["INVOICES.fmb", "ORDERS.fmb"],
            read.Ledger!.Claims.Select(claim => claim.ModuleAlias).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Preparing_against_a_superseded_source_profile_version_is_refused()
    {
        StubGateway gateway = new();
        await using Harness harness = await Harness.CreateAsync(_root, gateway);

        PlatformResult<SourcePreparationReport> result = await harness.PrepareAsync(
            ["ORDERS.fmb"], profileVersion: harness.ProfileVersion - 1);

        Assert.False(result.Succeeded);
        Assert.Equal(409, result.Status);
        Assert.Contains("superseded", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(gateway.Requests);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("forms/ORDERS.fmb")]
    [InlineData("C:\\forms\\ORDERS.fmb")]
    [InlineData("ORDERS.fmb\u0000.txt")]
    [InlineData("..fmb")]
    [InlineData("ORDERS.exe")]
    [InlineData("ORDERS")]
    public async Task A_module_name_that_is_not_a_plain_forms_file_name_never_reaches_the_gateway(string alias)
    {
        StubGateway gateway = new();
        await using Harness harness = await Harness.CreateAsync(_root, gateway);

        PlatformResult<SourcePreparationReport> result = await harness.PrepareAsync([alias]);

        Assert.False(result.Succeeded);
        Assert.Equal(400, result.Status);
        Assert.Empty(gateway.Requests);
    }

    [Fact]
    public async Task A_module_that_is_not_in_the_selected_folder_is_reported_and_never_requested()
    {
        StubGateway gateway = new();
        await using Harness harness = await Harness.CreateAsync(_root, gateway);

        PlatformResult<SourcePreparationReport> result = await harness.PrepareAsync(["ABSENT.fmb"]);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(0, result.Value!.ExtractedCount);
        Assert.False(result.Value.SnapshotRefreshed);
        Assert.Contains("not in the selected source folder", Assert.Single(result.Value.Modules).Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(gateway.Requests);
    }

    [Fact]
    public async Task A_replayed_answer_for_another_module_is_refused_and_nothing_is_written()
    {
        StubGateway gateway = new() { AnswerAlias = "INVOICES.fmb" };
        await using Harness harness = await Harness.CreateAsync(_root, gateway);
        string beforeHash = harness.SnapshotHash();

        PlatformResult<SourcePreparationReport> result = await harness.PrepareAsync(["ORDERS.fmb"]);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(0, result.Value!.ExtractedCount);
        Assert.False(result.Value.SnapshotRefreshed);
        Assert.Contains("not correlated", Assert.Single(result.Value.Modules).Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(harness.WorkspacePath, "forms", ".fleet-source")));
        Assert.Equal(beforeHash, harness.SnapshotHash());
    }

    [Fact]
    public async Task An_artifact_claiming_normalization_is_refused_and_nothing_is_written()
    {
        StubGateway gateway = new() { ClaimNormalized = true };
        await using Harness harness = await Harness.CreateAsync(_root, gateway);

        PlatformResult<SourcePreparationReport> result = await harness.PrepareAsync(["ORDERS.fmb"]);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(0, result.Value!.ExtractedCount);
        Assert.Contains("normalization phase", Assert.Single(result.Value.Modules).Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(harness.WorkspacePath, "forms", ".fleet-source")));
    }

    [Fact]
    public async Task A_transport_failure_is_reported_verbatim_and_produces_no_artifact()
    {
        StubGateway gateway = new() { TransportError = "The source gateway answered with a redirect, which is never followed." };
        await using Harness harness = await Harness.CreateAsync(_root, gateway);

        PlatformResult<SourcePreparationReport> result = await harness.PrepareAsync(["ORDERS.fmb"]);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(0, result.Value!.ExtractedCount);
        Assert.Contains("redirect", Assert.Single(result.Value.Modules).Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_admission_is_recorded_outside_the_source_copy_and_pins_the_module_digest()
    {
        StubGateway gateway = new();
        await using Harness harness = await Harness.CreateAsync(_root, gateway);

        PlatformResult<SourcePreparationReport> result = await harness.PrepareAsync(["ORDERS.fmb"]);
        Assert.True(result.Succeeded, result.Error);

        // The record lives beside the workspace folder, not inside it: everything inside arrived with the
        // operator's archive, so a claim kept there would be as forgeable as the artifact it vouches for.
        string ledger = PreparedSourceTrustStore.LedgerPath(harness.WorkspacePath)!;
        Assert.True(File.Exists(ledger));
        Assert.False(ledger.StartsWith(harness.WorkspacePath, StringComparison.Ordinal));

        PreparedSourceClaim claim = Assert.Single(
            PreparedSourceTrustStore.Read(harness.WorkspacePath, harness.OwnerBinding).Ledger!.Claims);
        PreparedModuleReport module = Assert.Single(result.Value!.Modules);

        Assert.Equal("ORDERS.fmb", claim.ModuleAlias);
        Assert.Equal(module.ContentSha256, claim.ModuleContentSha256);
        Assert.Equal(module.ArtifactSha256, claim.ArtifactSha256);
        Assert.Equal(module.ArtifactPath, claim.ArtifactPath);
        Assert.Equal(harness.ProfileHash, claim.ProfileHash);
        Assert.Equal(harness.ProfileVersion, claim.ProfileVersion);

        byte[] provenance = File.ReadAllBytes(Path.Combine(harness.WorkspacePath, claim.ProvenancePath.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal(claim.ProvenanceSha256, Convert.ToHexStringLower(SHA256.HashData(provenance)));
        Assert.Equal(claim.ProvenanceByteCount, provenance.Length);
    }

    [Fact]
    public async Task A_refused_answer_records_no_claim_so_nothing_in_the_copy_is_consumable()
    {
        StubGateway gateway = new() { ClaimNormalized = true };
        await using Harness harness = await Harness.CreateAsync(_root, gateway);

        await harness.PrepareAsync(["ORDERS.fmb"]);

        Assert.Empty(PreparedSourceTrustStore.Read(harness.WorkspacePath, harness.OwnerBinding).Ledger!.Claims);
    }

    [Fact]
    public async Task Preparing_one_module_twice_is_refused_and_the_admitted_artifact_is_left_alone()
    {
        StubGateway gateway = new();
        await using Harness harness = await Harness.CreateAsync(_root, gateway);

        PlatformResult<SourcePreparationReport> first = await harness.PrepareAsync(["ORDERS.fmb"]);
        byte[] admitted = File.ReadAllBytes(Path.Combine(
            harness.WorkspacePath, "forms", ".fleet-source", "extraction", "ORDERS.fmb.forms-ir.json"));

        PlatformResult<SourcePreparationReport> second = await harness.PrepareAsync(["ORDERS.fmb"]);

        Assert.True(second.Succeeded, second.Error);
        Assert.Equal(0, second.Value!.ExtractedCount);
        Assert.Contains("written once", Assert.Single(second.Value.Modules).Detail, StringComparison.OrdinalIgnoreCase);

        // One claim, and the bytes the first admission committed are still the bytes on disk.
        Assert.Single(PreparedSourceTrustStore.Read(harness.WorkspacePath, harness.OwnerBinding).Ledger!.Claims);
        Assert.Equal(
            Assert.Single(first.Value!.Modules).ArtifactSha256,
            Convert.ToHexStringLower(SHA256.HashData(admitted)));
    }

    // ---------------------------------------------------------------------------------------------
    // Reading the source database schema. The gateway is a stub for the same reason as above: what is
    // under test is what this host requests, admits, splits and keeps, never whether Oracle answered.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_a_configured_gateway_the_schema_operation_refuses_and_contacts_nothing()
    {
        await using Harness harness = await Harness.CreateAsync(_root, gateway: null);

        PlatformResult<SourceSchemaPreparationReport> result = await harness.PrepareSchemaAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(503, result.Status);
        Assert.Contains("No authorized source gateway is configured", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_only_schema_read_is_stored_as_statements_with_provenance_and_a_server_claim()
    {
        StubGateway gateway = new();
        await using Harness harness = await Harness.CreateAsync(_root, gateway);
        string beforeHash = harness.SnapshotHash();

        PlatformResult<SourceSchemaPreparationReport> result = await harness.PrepareSchemaAsync();

        Assert.True(result.Succeeded, result.Error);
        SourceSchemaPreparationReport report = result.Value!;
        Assert.Equal(SourceGatewayStatus.Extracted, report.Status);
        Assert.Equal(["LEGACY_LAB"], report.Schemas);
        Assert.Equal("forms/.fleet-source/extraction/legacy-order-entry.schema.sql", report.SchemaDdlPath);
        Assert.Equal("forms/.fleet-source/extraction/legacy-order-entry.program-units.sql", report.ProgramUnitPath);
        Assert.True(report.SnapshotRefreshed);

        // The statements on disk are the source's own, taken from the artifact and not rewritten.
        string ddl = File.ReadAllText(harness.PreparedPath("legacy-order-entry.schema.sql"));
        Assert.Contains("CREATE TABLE \"LEGACY_LAB\".\"ORDERS\"", ddl, StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT \"PK_ORDERS\"", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE OR REPLACE PROCEDURE", ddl, StringComparison.Ordinal);

        string plsql = File.ReadAllText(harness.PreparedPath("legacy-order-entry.program-units.sql"));
        Assert.StartsWith("ALTER SESSION SET CURRENT_SCHEMA = \"LEGACY_LAB\";", plsql, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE PROCEDURE PLACE_ORDER", plsql, StringComparison.Ordinal);

        // Provenance repeats what the gateway reported and refuses to imply adjudication or conversion.
        using JsonDocument provenance = JsonDocument.Parse(
            File.ReadAllBytes(harness.PreparedPath("legacy-order-entry.schema.provenance.json")));
        Assert.False(provenance.RootElement.GetProperty("normalized").GetBoolean());
        Assert.Equal(harness.ProfileHash, provenance.RootElement.GetProperty("profileHash").GetString());
        Assert.Equal(report.ArtifactSha256, provenance.RootElement.GetProperty("artifactSha256").GetString());

        // The claim lives beside the workspace folder, never inside the copy it vouches for.
        string ledger = PreparedSchemaTrustStore.LedgerPath(harness.WorkspacePath)!;
        Assert.False(ledger.StartsWith(harness.WorkspacePath, StringComparison.Ordinal));
        PreparedSchemaClaim claim = Assert.Single(
            PreparedSchemaTrustStore.Read(harness.WorkspacePath, harness.OwnerBinding).Ledger!.Claims);
        Assert.Equal(report.SchemaDdlPath, claim.SchemaDdlPath);
        Assert.Equal(report.ProgramUnitPath, claim.ProgramUnitPath);
        Assert.Equal(harness.ProfileVersion, claim.ProfileVersion);
        Assert.Equal(
            claim.SchemaDdlSha256,
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(harness.PreparedPath("legacy-order-entry.schema.sql")))));

        Assert.NotEqual(beforeHash, harness.SnapshotHash());
    }

    [Fact]
    public async Task The_statements_written_are_indexed_as_a_schema_export_and_as_program_units()
    {
        StubGateway gateway = new();
        await using Harness harness = await Harness.CreateAsync(_root, gateway);

        Assert.True((await harness.PrepareSchemaAsync()).Succeeded);

        SourceInventory inventory = SourceInventory.Build(harness.WorkspacePath, 10_000, 64L * 1024 * 1024);
        Assert.Contains(inventory.Artifacts, artifact => artifact.Kind == "DatabaseSchemaExport");
        Assert.Contains(inventory.Artifacts, artifact => artifact.Kind == "PlSqlProgramUnit");
    }

    [Fact]
    public async Task The_schemas_read_are_the_stored_profile_allowlist_and_never_a_caller_value()
    {
        StubGateway gateway = new();
        await using Harness harness = await Harness.CreateAsync(_root, gateway);

        await harness.PrepareSchemaAsync();

        SourceGatewayOracleSchemaRequest sent = Assert.Single(gateway.SchemaRequests);
        Assert.Equal(["LEGACY_LAB"], sent.SchemaAllowlist);
        Assert.Equal(harness.ProfileHash, sent.ProfileHash);
        Assert.Equal(harness.ProfileVersion, sent.ProfileVersion);
        Assert.Equal(SourceGatewayProtocol.SchemaVersion, sent.SchemaVersion);
    }

    [Theory]
    [InlineData("outside-allowlist")]
    [InlineData("tampered")]
    [InlineData("claims-normalized")]
    [InlineData("credential-finding")]
    [InlineData("program-units-without-a-section")]
    [InlineData("transport-failure")]
    [InlineData("blocked-prerequisite")]
    public async Task A_schema_answer_this_host_will_not_admit_writes_nothing_and_records_no_claim(string flaw)
    {
        StubGateway gateway = flaw switch
        {
            "outside-allowlist" => new StubGateway { AnswerSchemas = ["LEGACY_LAB", "SYS"] },
            "tampered" => new StubGateway { TamperSchemaDigest = true },
            "claims-normalized" => new StubGateway { SchemaClaimsNormalized = true },
            "credential-finding" => new StubGateway { SchemaFindings = ["connected with password=hunter2"] },
            "program-units-without-a-section" => new StubGateway { DropProgramUnitSection = true },
            "transport-failure" => new StubGateway { SchemaTransportError = "The source gateway could not be reached." },
            _ => new StubGateway { SchemaStatus = SourceGatewayStatus.BlockedPrerequisite },
        };

        await using Harness harness = await Harness.CreateAsync(_root, gateway);
        string beforeHash = harness.SnapshotHash();

        PlatformResult<SourceSchemaPreparationReport> result = await harness.PrepareSchemaAsync();

        // A refusal is still a typed report about the source, not a transport error the console must guess at.
        Assert.True(result.Succeeded, result.Error);
        Assert.NotEqual(SourceGatewayStatus.Extracted, result.Value!.Status);
        Assert.Null(result.Value.SchemaDdlPath);
        Assert.Null(result.Value.ProgramUnitPath);
        Assert.False(result.Value.SnapshotRefreshed);

        Assert.Empty(PreparedSchemaTrustStore.Read(harness.WorkspacePath, harness.OwnerBinding).Ledger!.Claims);
        Assert.False(Directory.Exists(Path.Combine(harness.WorkspacePath, Harness.SourceRoot, ".fleet-source")));
        Assert.Equal(beforeHash, harness.SnapshotHash());
    }

    [Fact]
    public async Task Reading_a_schema_against_a_superseded_source_profile_version_is_refused()
    {
        StubGateway gateway = new();
        await using Harness harness = await Harness.CreateAsync(_root, gateway);

        PlatformResult<SourceSchemaPreparationReport> result =
            await harness.PrepareSchemaAsync(profileVersion: harness.ProfileVersion - 1);

        Assert.False(result.Succeeded);
        Assert.Equal(409, result.Status);
        Assert.Contains("superseded", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(gateway.SchemaRequests);
    }

    [Fact]
    public async Task A_non_member_learns_nothing_about_the_schema_and_the_gateway_is_not_contacted()
    {
        StubGateway gateway = new();
        await using Harness harness = await Harness.CreateAsync(_root, gateway);

        PlatformResult<SourceSchemaPreparationReport> result = await harness.PrepareSchemaAsync(actor: Harness.Outsider);

        Assert.False(result.Succeeded);
        Assert.Equal(404, result.Status);
        Assert.Empty(gateway.SchemaRequests);
    }

    [Fact]
    public async Task An_operator_supplied_export_has_no_database_to_read_and_no_module_to_open()
    {
        StubGateway gateway = new();
        await using Harness harness = await Harness.CreateAsync(_root, gateway);
        SourceEnvironmentProfile republished = await harness.RedeclareAsync(
            SourceConnector.OperatorSuppliedExport, ["LEGACY_LAB"]);

        PlatformResult<SourceSchemaPreparationReport> schema =
            await harness.PrepareSchemaAsync(profileVersion: republished.Version);
        PlatformResult<SourcePreparationReport> modules =
            await harness.PrepareAsync(["ORDERS.fmb"], profileVersion: republished.Version);

        Assert.Equal(409, schema.Status);
        Assert.Equal(409, modules.Status);
        Assert.Empty(gateway.SchemaRequests);
        Assert.Empty(gateway.Requests);
    }

    [Fact]
    public async Task A_database_reader_may_read_a_schema_and_has_no_Forms_module_to_open()
    {
        StubGateway gateway = new();
        await using Harness harness = await Harness.CreateAsync(_root, gateway);
        SourceEnvironmentProfile republished = await harness.RedeclareAsync(
            SourceConnector.OracleDatabaseReader, ["LEGACY_LAB"]);

        PlatformResult<SourceSchemaPreparationReport> schema =
            await harness.PrepareSchemaAsync(profileVersion: republished.Version);
        PlatformResult<SourcePreparationReport> modules =
            await harness.PrepareAsync(["ORDERS.fmb"], profileVersion: republished.Version);

        Assert.True(schema.Succeeded, schema.Error);
        Assert.Equal(SourceGatewayStatus.Extracted, schema.Value!.Status);
        Assert.Equal(409, modules.Status);
        Assert.Empty(gateway.Requests);
    }

    [Fact]
    public async Task A_source_version_that_allows_no_schema_has_nothing_to_read()
    {
        StubGateway gateway = new();
        await using Harness harness = await Harness.CreateAsync(_root, gateway);
        SourceEnvironmentProfile republished = await harness.RedeclareAsync(SourceConnector.FormsBuilderWorker, []);

        PlatformResult<SourceSchemaPreparationReport> result =
            await harness.PrepareSchemaAsync(profileVersion: republished.Version);

        Assert.False(result.Succeeded);
        Assert.Equal(409, result.Status);
        Assert.Empty(gateway.SchemaRequests);
    }

    [Fact]
    public async Task Reading_one_source_environment_schema_twice_is_refused_and_the_first_statements_stand()
    {
        StubGateway gateway = new();
        await using Harness harness = await Harness.CreateAsync(_root, gateway);

        Assert.True((await harness.PrepareSchemaAsync()).Succeeded);
        byte[] admitted = File.ReadAllBytes(harness.PreparedPath("legacy-order-entry.schema.sql"));

        PlatformResult<SourceSchemaPreparationReport> second = await harness.PrepareSchemaAsync();

        Assert.True(second.Succeeded, second.Error);
        Assert.NotEqual(SourceGatewayStatus.Extracted, second.Value!.Status);
        Assert.Contains("written once", second.Value.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Single(PreparedSchemaTrustStore.Read(harness.WorkspacePath, harness.OwnerBinding).Ledger!.Claims);
        Assert.Equal(admitted, File.ReadAllBytes(harness.PreparedPath("legacy-order-entry.schema.sql")));
    }

    [Fact]
    public void An_authority_with_a_path_query_or_credential_is_not_a_configured_gateway()
    {        Assert.False(TryRead("https://gateway.example.com/source", "api://gateway/.default"));
        Assert.False(TryRead("https://gateway.example.com/?a=b", "api://gateway/.default"));
        Assert.False(TryRead("http://gateway.example.com/", "api://gateway/.default"));
        Assert.False(TryRead("https://user:secret@gateway.example.com/", "api://gateway/.default"));
        Assert.False(TryRead("https://gateway.example.com/", "password=hunter2"));
        Assert.False(TryRead("https://gateway.example.com/", null));
        Assert.True(TryRead("https://gateway.example.com/", "api://gateway/.default"));
    }

    private static bool TryRead(string? authority, string? scope)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SourceGateway:Authority"] = authority,
                ["SourceGateway:TokenScope"] = scope,
            })
            .Build();
        return SourceGatewayOptions.TryRead(configuration, out _, out _);
    }

    private static async Task<(int Status, string Detail)> PrepareThroughGatewayAsync(Harness harness, bool schema)
    {
        if (schema)
        {
            PlatformResult<SourceSchemaPreparationReport> result = await harness.PrepareSchemaAsync();
            return (result.Status, result.Succeeded ? result.Value!.Detail : result.Error);
        }

        PlatformResult<SourcePreparationReport> forms = await harness.PrepareAsync(["ORDERS.fmb"]);
        return (forms.Status, forms.Succeeded ? Assert.Single(forms.Value!.Modules).Detail : forms.Error);
    }

    private static void AssertNoPreparedCommit(Harness harness)
    {
        Assert.False(Directory.Exists(Path.Combine(harness.WorkspacePath, Harness.SourceRoot, ".fleet-source")));
        Assert.Empty(PreparedSourceTrustStore.Read(harness.WorkspacePath, harness.OwnerBinding).Ledger!.Claims);
        Assert.Empty(PreparedSchemaTrustStore.Read(harness.WorkspacePath, harness.OwnerBinding).Ledger!.Claims);
    }

    /// <summary>A gateway that answers exactly what a test tells it to, so admission is what is measured.</summary>
    private sealed class StubGateway : ISourceGatewayClient
    {
        public List<SourceGatewayFormsModuleRequest> Requests { get; } = [];

        public List<SourceGatewayOracleSchemaRequest> SchemaRequests { get; } = [];

        public byte[]? LastArtifact { get; private set; }

        public byte[]? LastSchemaArtifact { get; private set; }

        public string? AnswerAlias { get; init; }

        /// <summary>Set to echo a scope other than the one the host derived, which is how a replay looks.</summary>
        public SourceGatewayAuthorizationScope? AnswerScope { get; init; }

        /// <summary>Held by a test that needs the host to be mid-call while something else changes.</summary>
        public TaskCompletionSource? Gate { get; init; }

        /// <summary>Signalled once the gateway has been entered, so a test knows the call is in flight.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ClaimNormalized { get; init; }

        public string? TransportError { get; init; }

        /// <summary>Set to answer the schema half with something other than a clean extraction.</summary>
        public string? SchemaTransportError { get; init; }

        public string SchemaStatus { get; init; } = SourceGatewayStatus.Extracted;

        public string[]? AnswerSchemas { get; init; }

        public bool SchemaClaimsNormalized { get; init; }

        /// <summary>Declares a digest the artifact does not hash to, which is what a tampered body looks like.</summary>
        public bool TamperSchemaDigest { get; init; }

        /// <summary>Reports program units in the coverage while emitting no program-unit section.</summary>
        public bool DropProgramUnitSection { get; init; }

        public string[] SchemaFindings { get; init; } = [];

        public string Description => "Stub source gateway for offline tests.";

        public async Task<SourceGatewaySchemaCall> ExtractOracleSchemaAsync(
            SourceGatewayOracleSchemaRequest request,
            CancellationToken cancellationToken)
        {
            SchemaRequests.Add(request);
            await PauseAsync(cancellationToken);
            if (SchemaTransportError is { } error)
            {
                return new SourceGatewaySchemaCall(null, error);
            }

            string[] schemas = AnswerSchemas ?? [.. request.SchemaAllowlist];
            byte[] artifact = SchemaArtifact(request, schemas, SchemaClaimsNormalized, DropProgramUnitSection);
            LastSchemaArtifact = artifact;

            string digest = Convert.ToHexStringLower(SHA256.HashData(artifact));
            string declared = TamperSchemaDigest ? new string('b', 64) : digest;
            bool extracted = SchemaStatus == SourceGatewayStatus.Extracted;

            return new SourceGatewaySchemaCall(
                new SourceGatewayOracleSchemaResponse(
                    SourceGatewayProtocol.SchemaVersion,
                    SchemaStatus,
                    request.SourceEnvironmentId,
                    request.ProfileVersion,
                    request.ProfileHash,
                    s_now,
                    extracted ? declared : null,
                    extracted
                        ? new SourceGatewayArtifact(
                            SourceGatewayProtocol.OracleSchemaMediaType, declared, Convert.ToBase64String(artifact))
                        : null,
                    [],
                    SchemaFindings,
                    AnswerScope ?? request.Scope),
                null);
        }

        /// <summary>Preparation never probes; a stub that answered one would be asserting nothing.</summary>
        public Task<SourceGatewayProbeCall> ProbeSourceEnvironmentAsync(
            SourceGatewayProbeRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SourceGatewayProbeCall(null, "This stub gateway answers no probe."));

        /// <summary>Blocks the call until a test releases it, so "while the gateway was working" is real.</summary>
        private async Task PauseAsync(CancellationToken cancellationToken)
        {
            if (Gate is null)
            {
                return;
            }

            Entered.TrySetResult();
            await Gate.Task.WaitAsync(cancellationToken);
        }

        /// <summary>The artifact body as the worker's Oracle catalog reader writes it.</summary>
        public static byte[] SchemaArtifact(
            SourceGatewayOracleSchemaRequest request,
            IReadOnlyList<string> schemas,
            bool claimNormalized = false,
            bool dropProgramUnits = false)
        {
            string ddl = string.Concat(schemas.Select(schema =>
                $"""
                CREATE SEQUENCE "{schema}"."SEQ_ORDER_ID" START WITH 1 INCREMENT BY 1 MINVALUE 1 MAXVALUE 9999 CACHE 20 NOCYCLE NOORDER;

                CREATE TABLE "{schema}"."ORDERS" (
                  "ORDER_ID" NUMBER(10,0) NOT NULL
                );

                ALTER TABLE "{schema}"."ORDERS" ADD CONSTRAINT "PK_ORDERS" PRIMARY KEY ("ORDER_ID");


                """.Replace("\r\n", "\n", StringComparison.Ordinal)));

            if (!dropProgramUnits)
            {
                ddl += string.Concat(schemas.Select(schema =>
                    $"""
                    ALTER SESSION SET CURRENT_SCHEMA = "{schema}";

                    CREATE OR REPLACE PROCEDURE PLACE_ORDER IS BEGIN NULL; END;
                    /

                    """.Replace("\r\n", "\n", StringComparison.Ordinal)));
            }

            // The inventory has to name exactly what the DDL writes: one table, the index its primary key
            // is built on, one sequence and one procedure per schema. The host reconciles the two.
            object[] objects =
            [
                .. schemas.Select(schema => new { schema, kind = "TABLE", name = "ORDERS" }),
                .. schemas.Select(schema => new { schema, kind = "INDEX", name = "PK_ORDERS" }),
                .. schemas.Select(schema => new { schema, kind = "SEQUENCE", name = "SEQ_ORDER_ID" }),
                .. schemas.Select(schema => new { schema, kind = "PROCEDURE", name = "PLACE_ORDER" }),
            ];

            return JsonSerializer.SerializeToUtf8Bytes(
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["generator"] = SourceGatewayProtocol.ExtractedSchemaGenerator,
                    ["schemaVersion"] = SourceGatewayProtocol.ExtractedSchemaBodyVersion,
                    ["sourceEnvironmentId"] = request.SourceEnvironmentId,
                    ["profileVersion"] = request.ProfileVersion,
                    ["profileHash"] = request.ProfileHash,
                    ["schemas"] = schemas,
                    ["provider"] = "stub-odbc",
                    ["coverage"] = new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        ["objects"] = objects.Length,
                        ["tables"] = schemas.Count,
                        ["columns"] = schemas.Count,
                        ["constraints"] = schemas.Count,
                        ["sequences"] = schemas.Count,
                        ["indexes"] = schemas.Count,
                        ["grants"] = 0,
                        ["programUnits"] = schemas.Count,
                    },
                    ["objects"] = objects,
                    ["ddl"] = ddl,
                    ["normalized"] = claimNormalized ? true : null,
                }.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                SourceGatewayProtocol.Json);
        }

        public async Task<SourceGatewayCall> ExtractFormsModuleAsync(
            SourceGatewayFormsModuleRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            await PauseAsync(cancellationToken);
            if (TransportError is { } error)
            {
                return new SourceGatewayCall(null, error);
            }

            string alias = AnswerAlias ?? request.ModuleAlias;
            string adjudication = ClaimNormalized ? "\"normalized\":true," : string.Empty;
            byte[] artifact = Encoding.UTF8.GetBytes($$"""
                {"generator":"{{SourceGatewayProtocol.ExtractedIrGenerator}}",
                "schemaVersion":"{{SourceGatewayProtocol.ExtractedIrSchemaVersion}}",
                "sourceEnvironmentId":{{JsonSerializer.Serialize(request.SourceEnvironmentId)}},
                "profileVersion":{{request.ProfileVersion}},
                "profileHash":{{JsonSerializer.Serialize(request.ProfileHash)}},
                "moduleAlias":{{JsonSerializer.Serialize(alias)}},
                "contentSha256":{{JsonSerializer.Serialize(request.ExpectedContentSha256)}},
                "formsFamily":"Forms6i","versionAuthority":"source-worker-native",{{adjudication}}
                "modules":[{"name":"ORDERS","title":null,"blocks":[],"triggers":[],"programUnits":[],"lovs":[]}]}
                """);
            LastArtifact = artifact;

            return new SourceGatewayCall(
                new SourceGatewayFormsModuleResponse(
                    SourceGatewayProtocol.SchemaVersion,
                    SourceGatewayStatus.Extracted,
                    request.SourceEnvironmentId,
                    request.ExpectedFormsRelease,
                    request.ProfileVersion,
                    request.ProfileHash,
                    alias,
                    s_now,
                    "ORDERS",
                    request.ExpectedContentSha256,
                    new SourceGatewayArtifact(
                        SourceGatewayProtocol.FormsIrMediaType,
                        Convert.ToHexStringLower(SHA256.HashData(artifact)),
                        Convert.ToBase64String(artifact)),
                    null,
                    [],
                    [],
                    AnswerScope ?? request.Scope),
                null);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public const string ModuleContent = "FMB-bytes-of-the-orders-module";
        public const string SourceRoot = "forms";

        public static readonly WorkbenchActor Operator = WorkbenchActor.ForTenant(
            "tenant-a", "11111111-1111-1111-1111-111111111111", [WorkbenchRoles.MigrationOperator]);

        public static readonly WorkbenchActor SecondMember = WorkbenchActor.ForTenant(
            "tenant-a", "22222222-2222-2222-2222-222222222222", [WorkbenchRoles.MigrationOperator]);

        public static readonly WorkbenchActor Outsider = WorkbenchActor.ForTenant(
            "tenant-a", "33333333-3333-3333-3333-333333333333", [WorkbenchRoles.MigrationOperator]);

        private Harness(
            SourceWorkspaceService workspaces,
            PlatformAccessService platform,
            SourcePreparationService preparation,
            string projectId,
            string workspaceId,
            string workspacePath,
            SourceEnvironmentProfile profile)
        {
            Workspaces = workspaces;
            Platform = platform;
            Preparation = preparation;
            ProjectId = projectId;
            WorkspaceId = workspaceId;
            WorkspacePath = workspacePath;
            ProfileVersion = profile.Version;
            ProfileHash = profile.CanonicalHash;
            SourceEnvironmentId = profile.SourceEnvironmentId;
        }

        public SourceWorkspaceService Workspaces { get; }
        public PlatformAccessService Platform { get; }
        public SourcePreparationService Preparation { get; }
        public string ProjectId { get; }
        public string WorkspaceId { get; }
        public string WorkspacePath { get; }
        public string OwnerBinding =>
            PreparedSourceOwnerBinding.Derive(PlatformIdentity.WorkspaceOwner(Operator, ProjectId));
        public int ProfileVersion { get; }
        public string ProfileHash { get; }
        public string SourceEnvironmentId { get; }

        public static async Task<Harness> CreateAsync(string root, ISourceGatewayClient? gateway)
        {
            FilePlatformStateStore store = new(Path.Combine(root, "platform.json"));
            await store.InitializeAsync(CancellationToken.None);
            PlatformAccessService platform = new(store, sandbox: null, () => s_now);

            PlatformProject project = (await platform.CreateProjectAsync(Operator, "Legacy pilot", CancellationToken.None)).Value!;
            SourceEnvironmentProfile profile = (await platform.EnsureSourceEnvironmentProfileAsync(
                Operator,
                project.ProjectId,
                new SourceEnvironmentDeclaration(
                    "legacy-order-entry",
                    "Legacy Order Entry",
                    SourceConnector.FormsBuilderWorker,
                    "6i",
                    "9i",
                    "legacy-order-entry",
                    ["LEGACY_LAB"],
                    []),
                CancellationToken.None)).Value!;

            SourceWorkspaceService workspaces = new(Path.Combine(root, "workspaces"));
            string owner = PlatformIdentity.WorkspaceOwner(Operator, project.ProjectId);
            using MemoryStream archive = Archive(
                ($"{SourceRoot}/ORDERS.fmb", ModuleContent),
                ($"{SourceRoot}/INVOICES.fmb", "FMB-bytes-of-the-invoices-module"));

            string? workspaceId = null;
            await foreach (SourceProgress step in workspaces.ExtractAsync(owner, archive, "orders.zip", CancellationToken.None))
            {
                if (step.Level == "done") workspaceId = step.Text;
            }

            Assert.NotNull(workspaceId);
            return new Harness(
                workspaces,
                platform,
                new SourcePreparationService(platform, workspaces, gateway, () => s_now),
                project.ProjectId,
                workspaceId!,
                workspaces.ResolveRoot(owner, workspaceId!)!,
                profile);
        }

        public Task AddMemberAsync(WorkbenchActor member) =>
            Platform.AddMemberAsync(
                Operator, ProjectId, member.ObjectId, [WorkbenchRoles.MigrationOperator], CancellationToken.None);

        public async Task RevokeOperatorAsync()
        {
            PlatformMembership current = (await Platform.Store.GetMembershipAsync(
                Operator.TenantId, ProjectId, Operator.ObjectId, CancellationToken.None))!;
            PlatformMembership? revoked = await Platform.Store.UpsertMembershipAsync(
                current with { RemovedUtc = s_now }, current.Version, CancellationToken.None);
            Assert.NotNull(revoked);
        }

        public void ChangeSuppliedModuleBytes()
        {
            string path = Path.Combine(WorkspacePath, SourceRoot, "ORDERS.fmb");
            File.SetAttributes(path, FileAttributes.Normal);
            File.AppendAllText(path, "-changed-during-gateway-read");
        }

        public Task<PlatformResult<SourcePreparationReport>> PrepareAsync(
            IReadOnlyList<string> modules,
            WorkbenchActor? actor = null,
            int? profileVersion = null) =>
            Preparation.PrepareAsync(
                actor ?? Operator,
                ProjectId,
                new SourcePreparationRequest(
                    SourceEnvironmentId, profileVersion ?? ProfileVersion, WorkspaceId, SourceRoot, modules),
                CancellationToken.None);

        public Task<PlatformResult<SourceSchemaPreparationReport>> PrepareSchemaAsync(
            WorkbenchActor? actor = null,
            int? profileVersion = null) =>
            Preparation.PrepareSchemaAsync(
                actor ?? Operator,
                ProjectId,
                new SourceSchemaPreparationRequest(
                    SourceEnvironmentId, profileVersion ?? ProfileVersion, WorkspaceId, SourceRoot),
                CancellationToken.None);

        /// <summary>Publishes a new immutable version of the source environment, as the GUI would.</summary>
        public async Task<SourceEnvironmentProfile> RedeclareAsync(
            SourceConnector connector,
            IReadOnlyList<string> schemaAllowlist)
        {
            PlatformResult<SourceEnvironmentProfile> result = await Platform.EnsureSourceEnvironmentProfileAsync(
                Operator,
                ProjectId,
                new SourceEnvironmentDeclaration(
                    SourceEnvironmentId,
                    "Legacy Order Entry",
                    connector,
                    "6i",
                    "9i",
                    "legacy-order-entry",
                    schemaAllowlist,
                    []),
                CancellationToken.None);

            Assert.True(result.Succeeded, result.Error);
            return result.Value!;
        }

        public string PreparedPath(string fileName) =>
            Path.Combine(WorkspacePath, SourceRoot, ".fleet-source", "extraction", fileName);

        /// <summary>The server-only source identity, read the same way an authorization check reads it.</summary>
        public string SnapshotHash() =>
            Workspaces.Describe(PlatformIdentity.WorkspaceOwner(Operator, ProjectId), WorkspaceId, SourceRoot)!.SnapshotHash;

        public ValueTask DisposeAsync()
        {
            Workspaces.Dispose();
            return ValueTask.CompletedTask;
        }

        private static MemoryStream Archive(params (string Path, string Content)[] entries)
        {
            MemoryStream buffer = new();
            using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach ((string path, string content) in entries)
                {
                    using StreamWriter writer = new(archive.CreateEntry(path).Open());
                    writer.Write(content);
                }
            }

            buffer.Position = 0;
            return buffer;
        }
    }
}
