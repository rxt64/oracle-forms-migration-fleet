using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Fleet.Execution.Adapters;
using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// The server side of decision-to-generation binding: who may issue an authorization, what it is bound
/// to, and what a generation may later be recorded as having produced.
///
/// The workspace here is a real directory a real normalization adapter wrote into, the ledger is ingested
/// from that run's own representation, and the generation is performed by the real conversion adapter. No
/// digest in these assertions is asserted by one side to the other: both ends compute them from bytes.
/// </summary>
public sealed class GenerationAuthorizationServiceTests : IDisposable
{
    private const string Tenant = "8f1e2b42-6a1d-4a24-9ad2-6c1b6a8f3d71";
    private const string Operator = "3b4c9a10-7d42-4f0e-9d51-2a61f0c4b8e3";
    private const string SourceRoot = "legacy/forms";
    private const string OutputRoot = ".fleet-run/out";
    private const string IrPath = $"{OutputRoot}/intermediate/forms-ir.json";
    private const string ManifestPath = $"{SourceRoot}/{TargetMappingReader.ConventionalPath}";
    private const string CoveragePath = $"{OutputRoot}/{GenerationCoverage.RecordPath}";

    private const string Snapshot = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string OtherSnapshot = "2222222222222222222222222222222222222222222222222222222222222222";

    private const string FormsExport = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Module xmlns="http://xmlns.oracle.com/Forms" version="12.2.1.4" FormsVersion="12.2.1.4">
          <FormModule Name="ORDER_ENTRY" Title="Order entry">
            <Trigger Name="WHEN-NEW-FORM-INSTANCE" TriggerText="BEGIN EXECUTE_QUERY; END;"/>
            <Block Name="ORDER_BLOCK" QueryDataSourceName="MRD_ORDER_HEAD" RecordsDisplayCount="10">
              <Item Name="ORD_NO" ItemType="Text Item" DataType="Number" ColumnName="ORD_NO" Prompt="Order" Required="true"/>
            </Block>
          </FormModule>
        </Module>
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ofm-genauth-{Guid.NewGuid():N}");
    private readonly List<string> _directoryLinks = [];

    private static readonly JsonSerializerOptions CoverageJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public void Dispose()
    {
        foreach (string link in _directoryLinks)
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task No_authorization_is_issued_while_the_mapped_source_is_undecided()
    {
        Harness harness = await HarnessAsync();
        string ledgerId = await IngestAsync(harness);

        PlatformResult<GenerationAuthorization> refused = await harness.Ledgers.AuthorizeGenerationAsync(
            harness.Actor, ledgerId, harness.RunId, CancellationToken.None);

        Assert.False(refused.Succeeded);
        Assert.Equal(409, refused.Status);
        Assert.Contains("has no recorded disposition", refused.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_issued_authorization_is_bound_to_the_exact_source_and_mapping_bytes()
    {
        Harness harness = await HarnessAsync();
        string ledgerId = await IngestAsync(harness);
        await DecideScopeAsync(harness, ledgerId);

        PlatformResult<GenerationAuthorization> issued = await harness.Ledgers.AuthorizeGenerationAsync(
            harness.Actor, ledgerId, harness.RunId, CancellationToken.None);

        Assert.True(issued.Succeeded, issued.Error);
        GenerationAuthorization authorization = issued.Value!;

        Assert.Equal(ledgerId, authorization.LedgerId);
        Assert.Equal(Tenant, authorization.TenantId);
        Assert.Equal(harness.RunId, authorization.RunId);
        Assert.Equal(Snapshot, authorization.SourceSnapshotHash);
        Assert.Equal(Sha256(harness.Absolute(IrPath)), authorization.IntermediateContentSha256);
        Assert.Equal(Sha256(harness.Absolute(ManifestPath)), authorization.MappingManifestSha256);
        Assert.NotEmpty(authorization.Scope);
        Assert.Equal(GenerationCoverage.ScopeDigest(authorization.Scope), authorization.ScopeDigest);

        // The whole ledger, not the part the mapping anchors on: a property declared beside the anchored
        // block is carried with its own decision rather than left out of the authorization.
        IReadOnlyList<DispositionLedgerEntry> entries =
            await harness.Store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None);
        Assert.Equal(entries.Count, authorization.LedgerEntryCount);
        Assert.Equal(entries.Count, authorization.Scope.Count);
        Assert.Contains(authorization.Scope, entry =>
            !GenerationCoverage.Covers(harness.AnchorPath, entry.ObjectPath) &&
            entry.Decision == DispositionDecision.Retire);

        // The decisions it carries are the ledger's, re-read rather than echoed back from the caller,
        // and each row's decision revision is the one derived from the decision the store holds now.
        Assert.All(authorization.Scope, entry => Assert.Equal(
            entries.Single(stored => stored.EntryId == entry.EntryId).DecisionRevision, entry.DecisionRevision));
        Assert.All(authorization.Scope, entry => Assert.NotNull(entry.MappingRuleId));
    }

    /// <summary>
    /// A decision moved after an operator reviewed it is the decision the phase generates under, because
    /// the phase asks at the moment it generates rather than acting on a value issued earlier.
    /// </summary>
    [Fact]
    public async Task A_decision_changed_before_generation_is_the_decision_the_phase_generates_under()
    {
        Harness harness = await HarnessAsync();
        string ledgerId = await IngestAsync(harness);
        await DecideScopeAsync(harness, ledgerId);
        await EnqueueRunAsync(harness, "run-2", Snapshot);

        // An authorization the host could have obtained and held on to.
        PlatformResult<GenerationAuthorization> stale = await harness.Ledgers.AuthorizeGenerationAsync(
            harness.Actor, ledgerId, "run-2", CancellationToken.None);
        Assert.True(stale.Succeeded, stale.Error);

        GenerationScopedDecision moved = stale.Value!.Scope[0];
        await DecideAsync(harness, ledgerId, await EntryAsync(harness, ledgerId, moved.EntryId), DispositionDecision.Defer);

        PhaseExecutionResult result = await GenerateAsync(harness, ledgerId, "run-2");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Findings, finding => finding.Contains("was deferred", StringComparison.Ordinal));
        Assert.False(File.Exists(harness.Absolute(CoveragePath)));
    }

    [Fact]
    public async Task An_actor_from_another_tenant_cannot_authorize_this_ledger()
    {
        Harness harness = await HarnessAsync();
        string ledgerId = await IngestAsync(harness);
        await DecideScopeAsync(harness, ledgerId);

        WorkbenchActor intruder = WorkbenchActor.ForTenant(
            "5d2a7c31-9e88-4a1b-8f47-0c2d6b91e4a5", Operator, [WorkbenchRoles.MigrationOperator]);

        PlatformResult<GenerationAuthorization> refused = await harness.Ledgers.AuthorizeGenerationAsync(
            intruder, ledgerId, harness.RunId, CancellationToken.None);

        Assert.False(refused.Succeeded);
        Assert.Equal(404, refused.Status);

        // And the phase that asks through that actor is told no, rather than generating unauthorized.
        MigrationRunRequest request = Request();
        PhasePlan plan = MigrationRunPlanner.Plan(request).Phases
            .Single(phase => phase.Phase == MigrationPhase.ApplicationCodeConversion);

        PhaseExecutionResult result = await new ApplicationCodeConversionAdapter().ExecuteAsync(
            new PhaseExecutionContext(harness.WorkspaceRoot, SourceRoot, OutputRoot, plan, request, (_, _) => { })
            {
                CompletedPhases =
                [
                    new PhaseOutcome(
                        MigrationPhase.SourceNormalization, PhaseStatus.Planned, PhaseExecutionState.Executed, [], [], null),
                ],
                AuthorizationProvider = new LedgerGenerationAuthorizationProvider(
                    harness.Ledgers, intruder, ledgerId, harness.RunId),
            },
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(File.Exists(harness.Absolute(CoveragePath)));
    }

    [Fact]
    public async Task An_authorization_is_refused_for_a_phase_it_was_not_issued_for()
    {
        Harness harness = await HarnessAsync();
        string ledgerId = await IngestAsync(harness);
        await DecideScopeAsync(harness, ledgerId);

        GenerationAuthorizationDecision decision = await new LedgerGenerationAuthorizationProvider(
                harness.Ledgers, harness.Actor, ledgerId, harness.RunId)
            .AuthorizeAsync(
                new GenerationAuthorizationRequest(
                    MigrationPhase.DatabaseConversion,
                    Sha256(harness.Absolute(IrPath)),
                    Sha256(harness.Absolute(ManifestPath)),
                    []),
                CancellationToken.None);

        Assert.Null(decision.Authorization);
        Assert.Contains(decision.Denials, denial => denial.Contains("no generation authority", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_authorization_is_refused_for_a_run_that_reads_other_source()
    {
        Harness harness = await HarnessAsync();
        string ledgerId = await IngestAsync(harness);
        await DecideScopeAsync(harness, ledgerId);
        await CompleteRunAsync(harness, "run-other", OtherSnapshot, []);

        PlatformResult<GenerationAuthorization> refused = await harness.Ledgers.AuthorizeGenerationAsync(
            harness.Actor, ledgerId, "run-other", CancellationToken.None);

        Assert.False(refused.Succeeded);

        // The newer run also supersedes the snapshot these decisions were about, so the ledger is stale
        // and nothing may be issued over it at all.
        Assert.Contains("superseded", refused.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_recorded_generation_is_hashed_by_the_server_and_verifies_nothing()
    {
        Harness harness = await HarnessAsync();
        string ledgerId = await IngestAsync(harness);
        await DecideScopeAsync(harness, ledgerId);
        await EnqueueRunAsync(harness, "run-2", Snapshot);

        PhaseExecutionResult generated = await GenerateAsync(harness, ledgerId, "run-2");
        Assert.True(generated.Succeeded, generated.FailureReason);

        await CompleteQueuedRunAsync(harness, "run-2", [Artifact(harness, "run-2", IrPath), Artifact(harness, "run-2", CoveragePath)]);

        PlatformResult<DispositionLedgerGeneration> recorded = await harness.Ledgers.RecordGenerationFromRunAsync(
            Tenant, ledgerId, "run-2", CancellationToken.None);

        Assert.True(recorded.Succeeded, recorded.Error);

        IReadOnlyList<DispositionLedgerEntry> entries =
            await harness.Store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None);

        // Every row the ledger holds, because the generation was covered by every row the ledger holds.
        Assert.Equal(entries.Count, recorded.Value!.EntriesRecorded);
        Assert.All(entries, entry => Assert.True(entry.IsGenerated));
        Assert.All(entries, entry => Assert.Equal(recorded.Value.OutputSetSha256, entry.GeneratedRefs[0].ContentSha256));

        // Generation is not execution. Nothing was verified, and the ledger still refuses to complete.
        Assert.All(entries, entry => Assert.Equal(DispositionVerificationStatus.NotExecuted, entry.Verification));

        PlatformResult<DispositionLedgerView> view =
            await harness.Ledgers.ViewAsync(harness.Actor, ledgerId, CancellationToken.None);
        Assert.Equal(0, view.Value!.Summary.Counts.Verified);
        Assert.False(view.Value.Summary.Completion.CanComplete);
        Assert.Contains(view.Value.Summary.Completion.Blockers, blocker =>
            blocker.Contains("not backed by a test that executed and passed", StringComparison.Ordinal));
    }

    /// <summary>
    /// A decision moved after the application was generated. The artifact is evidence about the decision
    /// that was taken then, and there is no artifact for the decision that stands now.
    /// </summary>
    [Fact]
    public async Task A_decision_changed_after_generation_records_nothing()
    {
        Harness harness = await HarnessAsync();
        string ledgerId = await IngestAsync(harness);
        await DecideScopeAsync(harness, ledgerId);
        await EnqueueRunAsync(harness, "run-2", Snapshot);

        Assert.True((await GenerateAsync(harness, ledgerId, "run-2")).Succeeded);
        await CompleteQueuedRunAsync(harness, "run-2", [Artifact(harness, "run-2", IrPath), Artifact(harness, "run-2", CoveragePath)]);

        IReadOnlyList<DispositionLedgerEntry> entries =
            await harness.Store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None);

        DispositionLedgerEntry moved = entries.First(entry =>
            GenerationCoverage.Covers(harness.AnchorPath, entry.Identity.ObjectPath));
        await DecideAsync(harness, ledgerId, moved, DispositionDecision.Defer);

        PlatformResult<DispositionLedgerGeneration> refused = await harness.Ledgers.RecordGenerationFromRunAsync(
            Tenant, ledgerId, "run-2", CancellationToken.None);

        Assert.False(refused.Succeeded);
        Assert.Equal(409, refused.Status);
        Assert.Contains("no longer the decisions that generation was covered by", refused.Error!, StringComparison.Ordinal);

        Assert.All(
            await harness.Store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None),
            entry => Assert.False(entry.IsGenerated));
    }

    [Fact]
    public async Task A_superseded_ledger_records_nothing()
    {
        Harness harness = await HarnessAsync();
        string ledgerId = await IngestAsync(harness);
        await DecideScopeAsync(harness, ledgerId);
        await EnqueueRunAsync(harness, "run-2", Snapshot);

        Assert.True((await GenerateAsync(harness, ledgerId, "run-2")).Succeeded);
        await CompleteQueuedRunAsync(harness, "run-2", [Artifact(harness, "run-2", IrPath), Artifact(harness, "run-2", CoveragePath)]);

        // A later completed run read different source, so these decisions describe superseded material.
        await CompleteRunAsync(harness, "run-3", OtherSnapshot, []);

        PlatformResult<DispositionLedgerGeneration> refused = await harness.Ledgers.RecordGenerationFromRunAsync(
            Tenant, ledgerId, "run-2", CancellationToken.None);

        Assert.False(refused.Succeeded);
        Assert.Equal(409, refused.Status);
        Assert.Contains("superseded", refused.Error!, StringComparison.Ordinal);

        Assert.All(
            await harness.Store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None),
            entry => Assert.False(entry.IsGenerated));
    }

    /// <summary>
    /// A coverage record whose bytes are intact and whose digest its run recorded, naming an output root
    /// the run never wrote to. The files it describes are some other run's files.
    /// </summary>
    [Fact]
    public async Task A_coverage_record_naming_another_output_root_records_nothing()
    {
        Harness harness = await HarnessAsync();
        string ledgerId = await IngestAsync(harness);
        await DecideScopeAsync(harness, ledgerId);
        await EnqueueRunAsync(harness, "run-2", Snapshot);

        Assert.True((await GenerateAsync(harness, ledgerId, "run-2")).Succeeded);

        GenerationCoverageRecord record = JsonSerializer.Deserialize<GenerationCoverageRecord>(
            await File.ReadAllBytesAsync(harness.Absolute(CoveragePath)), CoverageJson)!;

        await File.WriteAllBytesAsync(
            harness.Absolute(CoveragePath),
            JsonSerializer.SerializeToUtf8Bytes(record with { OutputRoot = ".fleet-run/elsewhere" }, CoverageJson));

        // Registered with the digest of the bytes as they now stand, so nothing fails on integrity first.
        await CompleteQueuedRunAsync(harness, "run-2", [Artifact(harness, "run-2", IrPath), Artifact(harness, "run-2", CoveragePath)]);

        PlatformResult<DispositionLedgerGeneration> refused = await harness.Ledgers.RecordGenerationFromRunAsync(
            Tenant, ledgerId, "run-2", CancellationToken.None);

        Assert.False(refused.Succeeded);
        Assert.Equal(409, refused.Status);
        Assert.Contains("output root this run did not write to", refused.Error!, StringComparison.Ordinal);

        Assert.All(
            await harness.Store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None),
            entry => Assert.False(entry.IsGenerated));
    }

    /// <summary>
    /// A coverage record that lists fewer files than the tier holds. The omitted file would ship with the
    /// application and be described by nothing.
    /// </summary>
    [Fact]
    public async Task A_coverage_record_that_omits_an_emitted_file_records_nothing()
    {
        Harness harness = await HarnessAsync();
        string ledgerId = await IngestAsync(harness);
        await DecideScopeAsync(harness, ledgerId);
        await EnqueueRunAsync(harness, "run-2", Snapshot);

        Assert.True((await GenerateAsync(harness, ledgerId, "run-2")).Succeeded);

        GenerationCoverageRecord record = JsonSerializer.Deserialize<GenerationCoverageRecord>(
            await File.ReadAllBytesAsync(harness.Absolute(CoveragePath)), CoverageJson)!;

        await File.WriteAllBytesAsync(
            harness.Absolute(CoveragePath),
            JsonSerializer.SerializeToUtf8Bytes(
                record with { OutputFiles = [.. record.OutputFiles.Skip(1)] }, CoverageJson));

        await CompleteQueuedRunAsync(harness, "run-2", [Artifact(harness, "run-2", IrPath), Artifact(harness, "run-2", CoveragePath)]);

        PlatformResult<DispositionLedgerGeneration> refused = await harness.Ledgers.RecordGenerationFromRunAsync(
            Tenant, ledgerId, "run-2", CancellationToken.None);

        Assert.False(refused.Succeeded);
        Assert.Equal(409, refused.Status);
        Assert.Contains("no longer digest to what the generation recorded", refused.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_generated_file_edited_after_the_phase_ran_records_nothing()
    {
        Harness harness = await HarnessAsync();
        string ledgerId = await IngestAsync(harness);
        await DecideScopeAsync(harness, ledgerId);
        await EnqueueRunAsync(harness, "run-2", Snapshot);

        Assert.True((await GenerateAsync(harness, ledgerId, "run-2")).Succeeded);
        await CompleteQueuedRunAsync(harness, "run-2", [Artifact(harness, "run-2", IrPath), Artifact(harness, "run-2", CoveragePath)]);

        string emitted = harness.Absolute($"{OutputRoot}/application/mapping-manifest.json");
        await File.WriteAllTextAsync(emitted, "{\"tampered\":true}");

        PlatformResult<DispositionLedgerGeneration> refused = await harness.Ledgers.RecordGenerationFromRunAsync(
            Tenant, ledgerId, "run-2", CancellationToken.None);

        Assert.False(refused.Succeeded);
        Assert.Equal(409, refused.Status);
        Assert.Contains("no longer digest to what the generation recorded", refused.Error!, StringComparison.Ordinal);

        IReadOnlyList<DispositionLedgerEntry> entries =
            await harness.Store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None);
        Assert.All(entries, entry => Assert.False(entry.IsGenerated));
    }

    [Fact]
    public async Task Generated_files_beneath_a_directory_link_outside_the_workspace_are_not_hashed()
    {
        Harness harness = await HarnessAsync();
        string ledgerId = await IngestAsync(harness);
        await DecideScopeAsync(harness, ledgerId);
        await EnqueueRunAsync(harness, "run-2", Snapshot);

        Assert.True((await GenerateAsync(harness, ledgerId, "run-2")).Succeeded);
        await CompleteQueuedRunAsync(harness, "run-2", [Artifact(harness, "run-2", IrPath), Artifact(harness, "run-2", CoveragePath)]);

        string applicationDirectory = harness.Absolute($"{OutputRoot}/application");
        string externalDirectory = Path.Combine(_root, "known-junction-target-generation");
        Directory.Move(applicationDirectory, externalDirectory);
        CreateDirectoryLink(applicationDirectory, externalDirectory);

        PlatformResult<DispositionLedgerGeneration> result = await harness.Ledgers.RecordGenerationFromRunAsync(
            Tenant, ledgerId, "run-2", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(410, result.Status);
        Assert.Contains("workspace bytes", result.Error, StringComparison.Ordinal);
        Assert.All(
            await harness.Store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None),
            entry => Assert.False(entry.IsGenerated));
    }

    // ---- the binding a run is accepted under ----

    /// <summary>
    /// The locator a run carries is checked against the project and the source the server resolved, at
    /// the moment the run is started, so an operator is told then rather than by a phase refusing later.
    /// It issues nothing: the phase still asks at the moment it generates.
    /// </summary>
    [Fact]
    public async Task A_run_binds_only_to_a_ledger_over_its_own_project_and_its_own_source()
    {
        Harness harness = await HarnessAsync();
        string ledgerId = await IngestAsync(harness);

        PlatformResult<DispositionLedger> bound = await harness.Ledgers.BindRunAsync(
            harness.Actor, ledgerId, harness.ProjectId, Snapshot, CancellationToken.None);
        Assert.True(bound.Succeeded, bound.Error);
        Assert.Equal(harness.ProjectId, bound.Value!.ProjectId);

        PlatformResult<DispositionLedger> otherSource = await harness.Ledgers.BindRunAsync(
            harness.Actor, ledgerId, harness.ProjectId, OtherSnapshot, CancellationToken.None);
        Assert.False(otherSource.Succeeded);
        Assert.Equal(409, otherSource.Status);
        Assert.Contains("different source snapshot", otherSource.Error!, StringComparison.Ordinal);

        // Which ledgers another project holds is not this caller's to learn from the status code.
        PlatformResult<DispositionLedger> otherProject = await harness.Ledgers.BindRunAsync(
            harness.Actor, ledgerId, "prj-somewhere-else", Snapshot, CancellationToken.None);
        Assert.False(otherProject.Succeeded);
        Assert.Equal(404, otherProject.Status);

        PlatformResult<DispositionLedger> unknown = await harness.Ledgers.BindRunAsync(
            harness.Actor, "dled-absent", harness.ProjectId, Snapshot, CancellationToken.None);
        Assert.False(unknown.Succeeded);
        Assert.Equal(404, unknown.Status);
    }

    [Fact]
    public async Task A_run_does_not_bind_to_a_superseded_ledger()
    {
        Harness harness = await HarnessAsync();
        string ledgerId = await IngestAsync(harness);
        await CompleteRunAsync(harness, "run-newer", OtherSnapshot, []);

        PlatformResult<DispositionLedger> bound = await harness.Ledgers.BindRunAsync(
            harness.Actor, ledgerId, harness.ProjectId, Snapshot, CancellationToken.None);

        Assert.False(bound.Succeeded);
        Assert.Equal(409, bound.Status);
        Assert.Contains("superseded", bound.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The durable worker records a generation from the manifest it hashed itself, while the run is still
    /// running, because the answer decides whether the run is a success at all. Recording from that
    /// manifest has to produce exactly what recording from the stored one produces.
    /// </summary>
    [Fact]
    public async Task A_generation_records_from_the_manifest_the_worker_hashed_before_the_run_is_terminal()
    {
        Harness harness = await HarnessAsync();
        string ledgerId = await IngestAsync(harness);
        await DecideScopeAsync(harness, ledgerId);
        await EnqueueRunAsync(harness, "run-2", Snapshot);

        Assert.True((await GenerateAsync(harness, ledgerId, "run-2")).Succeeded);

        MigrationRunClaim claim = (await harness.Runs.ClaimAsync(
            "node-a", "worker-a", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), CancellationToken.None))!;
        Assert.True(await harness.Runs.MarkRunningAsync(
            "run-2", claim.FenceToken, DateTimeOffset.UtcNow, CancellationToken.None));

        MigrationRunArtifact[] manifest =
            [Artifact(harness, "run-2", IrPath), Artifact(harness, "run-2", CoveragePath)];

        PlatformResult<DispositionLedgerGeneration> recorded = await harness.Ledgers.RecordGenerationFromRunAsync(
            Tenant, ledgerId, "run-2", manifest, CancellationToken.None);

        Assert.True(recorded.Succeeded, recorded.Error);

        IReadOnlyList<DispositionLedgerEntry> entries =
            await harness.Store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None);
        Assert.Equal(entries.Count, recorded.Value!.EntriesRecorded);
        Assert.All(entries, entry => Assert.True(entry.IsGenerated));
        Assert.All(entries, entry => Assert.Equal(DispositionVerificationStatus.NotExecuted, entry.Verification));
    }

    /// <summary>
    /// A worker whose claim a newer one displaced no longer speaks for the run. Recording re-establishes
    /// ownership against the run store rather than trusting the claim it made earlier, so the displaced
    /// worker is refused and no decision acquires a reference to what it produced.
    /// </summary>
    [Fact]
    public async Task A_worker_whose_claim_was_displaced_records_nothing()
    {
        Harness harness = await HarnessAsync();
        string ledgerId = await IngestAsync(harness);
        await DecideScopeAsync(harness, ledgerId);
        await EnqueueRunAsync(harness, "run-2", Snapshot);

        Assert.True((await GenerateAsync(harness, ledgerId, "run-2")).Succeeded);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        MigrationRunClaim claim = (await harness.Runs.ClaimAsync(
            "node-a", "worker-a", now, TimeSpan.FromSeconds(1), CancellationToken.None))!;
        Assert.True(await harness.Runs.MarkRunningAsync("run-2", claim.FenceToken, now, CancellationToken.None));

        // The lease lapses and the run is reconciled, which advances the fence past the one this worker
        // holds. Everything it hashed is still on disk and still digests correctly.
        Assert.Equal(1, await harness.Runs.ReconcileExpiredAsync(
            MigrationRunNode.Current, now.AddHours(1), "The prior worker lease expired.", CancellationToken.None));

        PlatformResult<DispositionLedgerGeneration> refused = await harness.Ledgers.RecordGenerationFromRunAsync(
            Tenant,
            ledgerId,
            "run-2",
            [Artifact(harness, "run-2", IrPath), Artifact(harness, "run-2", CoveragePath)],
            new MigrationRunOwnership(claim.FenceToken, TimeSpan.FromSeconds(45)),
            CancellationToken.None);

        Assert.False(refused.Succeeded);
        Assert.Equal(409, refused.Status);
        Assert.Contains("no longer held by the worker", refused.Error!, StringComparison.Ordinal);

        Assert.All(
            await harness.Store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None),
            entry => Assert.False(entry.IsGenerated));
    }

    /// <summary>
    /// The displaced worker that gets its write in first. Renewing the fence and writing the ledger are
    /// two stores and not one transaction, so the reference carries the fence it was written under and
    /// the server's own read stops presenting it once the run is owned by someone else. Nothing is
    /// deleted: the row keeps what was written, and no reader is told a decision was generated.
    /// </summary>
    [Fact]
    public async Task A_reference_written_under_a_superseded_fence_is_not_read_back_as_evidence()
    {
        Harness harness = await HarnessAsync();
        string ledgerId = await IngestAsync(harness);
        await DecideScopeAsync(harness, ledgerId);
        await EnqueueRunAsync(harness, "run-2", Snapshot);

        Assert.True((await GenerateAsync(harness, ledgerId, "run-2")).Succeeded);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        MigrationRunClaim claim = (await harness.Runs.ClaimAsync(
            "node-a", "worker-a", now, TimeSpan.FromSeconds(1), CancellationToken.None))!;
        Assert.True(await harness.Runs.MarkRunningAsync("run-2", claim.FenceToken, now, CancellationToken.None));

        PlatformResult<DispositionLedgerGeneration> recorded = await harness.Ledgers.RecordGenerationFromRunAsync(
            Tenant,
            ledgerId,
            "run-2",
            [Artifact(harness, "run-2", IrPath), Artifact(harness, "run-2", CoveragePath)],
            new MigrationRunOwnership(claim.FenceToken, TimeSpan.FromSeconds(45)),
            CancellationToken.None);
        Assert.True(recorded.Succeeded, recorded.Error);

        PlatformResult<DispositionEntryPage> owned = await harness.Ledgers.EntriesAsync(
            harness.Actor, ledgerId, null, null, false, 0, DispositionLedgerService.MaxEntryPage, CancellationToken.None);
        Assert.All(owned.Value!.Entries, entry => Assert.True(entry.IsGenerated));

        Assert.Equal(1, await harness.Runs.ReconcileExpiredAsync(
            MigrationRunNode.Current, now.AddHours(1), "The prior worker lease expired.", CancellationToken.None));

        // The rows still hold what was written; what a reader is told changed.
        Assert.All(
            await harness.Store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None),
            entry => Assert.Equal(claim.FenceToken, entry.GeneratedRefs.Single().RunFenceToken));

        PlatformResult<DispositionEntryPage> displaced = await harness.Ledgers.EntriesAsync(
            harness.Actor, ledgerId, null, null, false, 0, DispositionLedgerService.MaxEntryPage, CancellationToken.None);
        Assert.All(displaced.Value!.Entries, entry => Assert.False(entry.IsGenerated));

        PlatformResult<DispositionLedgerView> view =
            await harness.Ledgers.ViewAsync(harness.Actor, ledgerId, CancellationToken.None);
        Assert.Equal(0, view.Value!.Summary.Counts.Generated);
    }

    /// <summary>
    /// A property that was generated, executed and passed, then generated again.
    ///
    /// The second generation replaces the artifact the result was proved against, so the result stops
    /// standing: the ledger reads generated and unverified, and refuses to complete until something runs
    /// against the output that now exists. A result from the superseded run cannot be re-attached to it,
    /// and recording the identical generation a second time changes nothing and drops nothing.
    /// </summary>
    [Fact]
    public async Task A_regeneration_drops_the_result_proved_against_the_output_it_replaced()
    {
        Harness harness = await HarnessAsync();
        string ledgerId = await IngestAsync(harness);
        await DecideScopeAsync(harness, ledgerId);
        await EnqueueRunAsync(harness, "run-2", Snapshot);

        Assert.True((await GenerateAsync(harness, ledgerId, "run-2")).Succeeded);
        await CompleteQueuedRunAsync(harness, "run-2", [Artifact(harness, "run-2", IrPath), Artifact(harness, "run-2", CoveragePath)]);
        Assert.True((await harness.Ledgers.RecordGenerationFromRunAsync(
            Tenant, ledgerId, "run-2", CancellationToken.None)).Succeeded);

        await PassEveryEntryAsync(harness, ledgerId, "run-2");

        DispositionLedgerSummary verified = (await harness.Ledgers.ViewAsync(
            harness.Actor, ledgerId, CancellationToken.None)).Value!.Summary;
        Assert.Equal(verified.Counts.Discovered, verified.Counts.Verified);
        Assert.True(verified.Completion.CanComplete);

        // The same source and the same decisions generated again under a second run. Recording it at all
        // is the separation of the decision revision from the row version: run-2's evidence rewrote every
        // row, and an authorization over decisions nobody touched still describes those decisions.
        await EnqueueRunAsync(harness, "run-3", Snapshot);
        Assert.True((await GenerateAsync(harness, ledgerId, "run-3")).Succeeded);
        await CompleteQueuedRunAsync(harness, "run-3", [Artifact(harness, "run-3", IrPath), Artifact(harness, "run-3", CoveragePath)]);

        PlatformResult<DispositionLedgerGeneration> regenerated = await harness.Ledgers.RecordGenerationFromRunAsync(
            Tenant, ledgerId, "run-3", CancellationToken.None);
        Assert.True(regenerated.Succeeded, regenerated.Error);

        DispositionLedgerSummary replaced = (await harness.Ledgers.ViewAsync(
            harness.Actor, ledgerId, CancellationToken.None)).Value!.Summary;
        Assert.Equal(replaced.Counts.Discovered, replaced.Counts.Generated);
        Assert.Equal(0, replaced.Counts.Verified);
        Assert.False(replaced.Completion.CanComplete);
        Assert.Contains(replaced.Completion.Blockers, blocker =>
            blocker.Contains("not backed by a test that executed and passed", StringComparison.Ordinal));

        // A result executed against the output run-2 produced is not a result about the output that
        // replaced it, so it cannot be re-attached to make the new image look proved.
        DispositionLedgerEntry entry =
            (await harness.Store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None))[0];

        PlatformResult<DispositionLedgerEntry> superseded = await harness.Ledgers.RecordVerificationAsync(
            Tenant,
            ledgerId,
            new DispositionVerificationEvidence("run-2", entry.EntryId, [],
                [new DispositionTestReference(
                    "run-2", "GeneratedSurfaceTest#stale", DispositionVerificationStatus.Passed, "1 assertion",
                    DateTimeOffset.UtcNow, entry.DecisionRevision, entry.GeneratedRefs[0].ContentSha256,
                    entry.SourceSnapshotHash, entry.GeneratedRefs[0].RunFenceToken)]),
            CancellationToken.None);

        Assert.False(superseded.Succeeded);
        Assert.Equal(409, superseded.Status);
        Assert.Contains("not the one this run owns", superseded.Error!, StringComparison.Ordinal);

        // Retested against what now exists, then recorded a second time from the identical generation.
        await PassEveryEntryAsync(harness, ledgerId, "run-3");

        PlatformResult<DispositionLedgerGeneration> repeated = await harness.Ledgers.RecordGenerationFromRunAsync(
            Tenant, ledgerId, "run-3", CancellationToken.None);

        Assert.True(repeated.Succeeded, repeated.Error);
        Assert.Equal(0, repeated.Value!.EntriesRecorded);

        DispositionLedgerSummary unchanged = (await harness.Ledgers.ViewAsync(
            harness.Actor, ledgerId, CancellationToken.None)).Value!.Summary;
        Assert.Equal(unchanged.Counts.Discovered, unchanged.Counts.Verified);
        Assert.True(unchanged.Completion.CanComplete);
    }

    /// <summary>
    /// The result half of the displaced-owner case. Suppressing the artifact a displaced worker wrote has
    /// to suppress what was proved about it too, or the row reads verified with no admissible generation
    /// behind it — a property reported migrated and proved on the strength of nothing a reader is shown.
    /// </summary>
    [Fact]
    public async Task A_result_proved_under_a_superseded_fence_is_not_read_back_as_verified()
    {
        Harness harness = await HarnessAsync();
        string ledgerId = await IngestAsync(harness);
        await DecideScopeAsync(harness, ledgerId);
        await EnqueueRunAsync(harness, "run-2", Snapshot);

        Assert.True((await GenerateAsync(harness, ledgerId, "run-2")).Succeeded);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        MigrationRunClaim claim = (await harness.Runs.ClaimAsync(
            "node-a", "worker-a", now, TimeSpan.FromSeconds(1), CancellationToken.None))!;
        Assert.True(await harness.Runs.MarkRunningAsync("run-2", claim.FenceToken, now, CancellationToken.None));

        Assert.True((await harness.Ledgers.RecordGenerationFromRunAsync(
            Tenant,
            ledgerId,
            "run-2",
            [Artifact(harness, "run-2", IrPath), Artifact(harness, "run-2", CoveragePath)],
            new MigrationRunOwnership(claim.FenceToken, TimeSpan.FromSeconds(45)),
            CancellationToken.None)).Succeeded);

        await PassEveryEntryAsync(harness, ledgerId, "run-2");

        DispositionLedgerSummary owned = (await harness.Ledgers.ViewAsync(
            harness.Actor, ledgerId, CancellationToken.None)).Value!.Summary;
        Assert.Equal(owned.Counts.Discovered, owned.Counts.Verified);

        Assert.Equal(1, await harness.Runs.ReconcileExpiredAsync(
            MigrationRunNode.Current, now.AddHours(1), "The prior worker lease expired.", CancellationToken.None));

        // The rows still hold the passing result; what a reader is told changed.
        Assert.All(
            await harness.Store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None),
            entry => Assert.Equal(DispositionVerificationStatus.Passed, entry.Verification));

        DispositionLedgerSummary displaced = (await harness.Ledgers.ViewAsync(
            harness.Actor, ledgerId, CancellationToken.None)).Value!.Summary;
        Assert.Equal(0, displaced.Counts.Generated);
        Assert.Equal(0, displaced.Counts.Verified);
        Assert.False(displaced.Completion.CanComplete);

        PlatformResult<DispositionEntryPage> page = await harness.Ledgers.EntriesAsync(
            harness.Actor, ledgerId, null, null, false, 0, DispositionLedgerService.MaxEntryPage, CancellationToken.None);

        Assert.All(page.Value!.Entries, entry =>
        {
            Assert.Empty(entry.TestRefs);
            Assert.Empty(entry.GeneratedRefs);
            Assert.Equal(DispositionVerificationStatus.NotExecuted, entry.Verification);
        });
    }

    [Fact]
    public async Task A_completed_normalization_ledger_authorizes_a_second_durable_generation_run()
    {
        Harness harness = await HarnessAsync(useActualSnapshot: true);
        string ledgerId = await IngestAsync(harness);
        await DecideScopeAsync(harness, ledgerId);
        const string runId = "run-worker-generation";
        await EnqueueRunAsync(harness, runId, harness.SourceSnapshotHash, ledgerId, MigrationRunNode.Current);

        await using ServiceProvider services = new ServiceCollection()
            .AddSingleton(harness.Platform)
            .AddSingleton(harness.Ledgers)
            .AddSingleton<IApplicationBuildGateway, SuccessfulBuildGateway>()
            .BuildServiceProvider();
        using SourceWorkspaceService workspaces = new(Path.GetDirectoryName(harness.WorkspaceRoot));
        MigrationRunWorker worker = new(
            harness.Runs,
            workspaces,
            new WorkbenchAuthorizationService(),
            services,
            NullLogger<MigrationRunWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        MigrationRunRecord terminal;
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            do
            {
                await Task.Delay(50, timeout.Token);
                terminal = (await harness.Runs.GetAsync(Tenant, runId, timeout.Token))!;
            }
            while (!terminal.IsTerminal);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        IReadOnlyList<MigrationRunEvent> events =
            await harness.Runs.EventsAsync(Tenant, runId, 0, CancellationToken.None);
        string diagnostics = JsonSerializer.Serialize(new
        {
            terminal.FailureReason,
            terminal.Outcome?.Phases,
            Events = events.Select(item => item.Text),
        });
        Assert.True(
            terminal.State == MigrationRunState.Succeeded,
            $"Expected a succeeded durable run, got {terminal.State}: {diagnostics}");
        Assert.Null(terminal.FailureReason);
        Assert.Contains(
            await harness.Runs.ArtifactsAsync(Tenant, runId, CancellationToken.None),
            artifact =>
            string.Equals(artifact.Path, CoveragePath, StringComparison.Ordinal));

        IReadOnlyList<DispositionLedgerEntry> entries =
            await harness.Store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None);
        Assert.All(entries, entry => Assert.True(entry.IsGenerated));
    }

    /// <summary>A manifest describing some other run's artifacts is not this run's evidence.</summary>
    [Fact]
    public async Task A_manifest_naming_another_run_records_nothing()
    {
        Harness harness = await HarnessAsync();
        string ledgerId = await IngestAsync(harness);
        await DecideScopeAsync(harness, ledgerId);
        await EnqueueRunAsync(harness, "run-2", Snapshot);

        Assert.True((await GenerateAsync(harness, ledgerId, "run-2")).Succeeded);

        PlatformResult<DispositionLedgerGeneration> refused = await harness.Ledgers.RecordGenerationFromRunAsync(
            Tenant,
            ledgerId,
            "run-2",
            [Artifact(harness, "run-elsewhere", CoveragePath)],
            CancellationToken.None);

        Assert.False(refused.Succeeded);
        Assert.Equal(409, refused.Status);
        Assert.Contains("recorded no generation coverage", refused.Error!, StringComparison.Ordinal);

        Assert.All(
            await harness.Store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None),
            entry => Assert.False(entry.IsGenerated));
    }

    private static string Sha256(string absolutePath) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(absolutePath)));
    private static MigrationRunArtifact Artifact(Harness harness, string runId, string path)
    {
        byte[] bytes = File.ReadAllBytes(harness.Absolute(path));
        return new MigrationRunArtifact(
            runId, path, "GeneratedArtifact", "Run output", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static MigrationRunRequest Request() => new()
    {
        EngagementId = "ENG-AUTH",
        ApplicationName = "Meridian Order Entry",
        RequestedMode = ExecutionMode.GenerateArtifacts,
        Target = new TargetStack { BackEnd = BackEndStack.AspNetCore, Database = DatabaseTarget.PostgreSql },
        OracleFormsVersion = "12c",
        OracleDatabaseVersion = "19c",
        SourceRoot = SourceRoot,
        OutputRoot = OutputRoot,
    };

    private static Task<PhaseExecutionResult> GenerateAsync(Harness harness, string ledgerId, string runId)
    {
        MigrationRunRequest request = Request();
        PhasePlan plan = MigrationRunPlanner.Plan(request).Phases
            .Single(phase => phase.Phase == MigrationPhase.ApplicationCodeConversion);

        return new ApplicationCodeConversionAdapter().ExecuteAsync(
            new PhaseExecutionContext(harness.WorkspaceRoot, SourceRoot, OutputRoot, plan, request, (_, _) => { })
            {
                CompletedPhases =
                [
                    new PhaseOutcome(
                        MigrationPhase.SourceNormalization, PhaseStatus.Planned, PhaseExecutionState.Executed, [], [], null),
                ],
                AuthorizationProvider = new LedgerGenerationAuthorizationProvider(
                    harness.Ledgers, harness.Actor, ledgerId, runId),
            },
            CancellationToken.None);
    }

    private static async Task<string> IngestAsync(Harness harness)
    {
        PlatformResult<DispositionLedgerView> ingested = await harness.Ledgers.IngestFromRunAsync(
            harness.Actor, harness.ProjectId, harness.RunId, CancellationToken.None);
        Assert.True(ingested.Succeeded, ingested.Error);
        return ingested.Value!.Summary.LedgerId;
    }

    /// <summary>
    /// Decides every property the ledger holds, one operator decision at a time.
    ///
    /// Carried forward only where the .NET master/detail generator re-expresses the property: the mapped
    /// block's existence and the table it declares it queries, and the mapped item's existence and the
    /// column it declares it is bound to. Everything else this export declares reaches no emitted file,
    /// so it is retired under the rule that authorizes retiring — a decision about it, not an omission of
    /// it, and not a claim that something was generated from it.
    /// </summary>
    private static async Task DecideScopeAsync(Harness harness, string ledgerId)
    {
        IReadOnlyList<DispositionLedgerEntry> entries =
            await harness.Store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None);

        Assert.NotEmpty(entries);
        Assert.Contains(entries, entry => !GenerationCoverage.Covers(harness.AnchorPath, entry.Identity.ObjectPath));

        string itemPath = entries
            .Single(entry =>
                !string.Equals(entry.Identity.ObjectPath, harness.AnchorPath, StringComparison.Ordinal) &&
                GenerationCoverage.Covers(harness.AnchorPath, entry.Identity.ObjectPath) &&
                string.Equals(entry.Identity.PropertyName, TargetMappingReader.ColumnAttribute, StringComparison.Ordinal))
            .Identity.ObjectPath;

        foreach (DispositionLedgerEntry entry in entries)
        {
            bool carried =
                (string.Equals(entry.Identity.ObjectPath, harness.AnchorPath, StringComparison.Ordinal) &&
                    entry.Identity.PropertyName is DispositionLedgerEntries.ObjectPresenceProperty
                        or TargetMappingReader.DataSourceAttribute) ||
                (string.Equals(entry.Identity.ObjectPath, itemPath, StringComparison.Ordinal) &&
                    entry.Identity.PropertyName is DispositionLedgerEntries.ObjectPresenceProperty
                        or TargetMappingReader.ColumnAttribute);

            await DecideAsync(
                harness, ledgerId, entry, carried ? DispositionDecision.Preserve : DispositionDecision.Retire);
        }
    }

    private static async Task DecideAsync(
        Harness harness,
        string ledgerId,
        DispositionLedgerEntry entry,
        DispositionDecision decision)
    {
        (string rule, string rationale) = decision switch
        {
            DispositionDecision.Preserve => (
                "DR-PRESERVE-DECLARED", "The declared value is carried into the generated application unchanged."),
            DispositionDecision.Retire => (
                "DR-RETIRE-NO-TARGET", "Oracle Forms runtime machinery with no counterpart in the target stack."),
            DispositionDecision.Defer => (
                "DR-DEFER-NEEDS-EVIDENCE", "Cannot be disposed of until evidence this run does not have is available."),
            _ => (
                "DR-TRANSFORM-EQUIVALENT", "The declared value has a documented Azure-stack equivalent."),
        };

        PlatformResult<DispositionLedgerEntry> decided = await harness.Ledgers.DecideAsync(
            harness.Actor,
            ledgerId,
            entry.EntryId,
            new DispositionDecisionInput(decision, rationale, rule, 1, entry.Version),
            CancellationToken.None);

        Assert.True(decided.Succeeded, decided.Error);
    }

    private static async Task<DispositionLedgerEntry> EntryAsync(Harness harness, string ledgerId, string entryId)
    {
        IReadOnlyList<DispositionLedgerEntry> entries =
            await harness.Store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None);

        return entries.Single(entry => string.Equals(entry.EntryId, entryId, StringComparison.Ordinal));
    }

    private void CreateDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            ProcessStartInfo startInfo = new("cmd.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add("/J");
            startInfo.ArgumentList.Add(link);
            startInfo.ArgumentList.Add(target);

            using Process process = Process.Start(startInfo)!;
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }

        _directoryLinks.Add(link);
    }

    /// <summary>Records one passing test per row, bound to the generation the named run produced.</summary>
    private static async Task PassEveryEntryAsync(Harness harness, string ledgerId, string runId)
    {
        foreach (DispositionLedgerEntry entry in
            await harness.Store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None))
        {
            // The worker states what it actually ran against: the artifact standing under the decision
            // this row records, and the claim that artifact was written under.
            DispositionGeneratedReference standing = DispositionLedgerRules
                .StandingGeneration(entry.DecisionRevision, entry.GeneratedRefs)!;

            PlatformResult<DispositionLedgerEntry> passed = await harness.Ledgers.RecordVerificationAsync(
                Tenant,
                ledgerId,
                new DispositionVerificationEvidence(
                    runId,
                    entry.EntryId,
                    [],
                    [new DispositionTestReference(
                        runId,
                        $"GeneratedSurfaceTest#{entry.EntryId}",
                        DispositionVerificationStatus.Passed,
                        "1 assertion",
                        DateTimeOffset.UtcNow,
                        entry.DecisionRevision,
                        standing.ContentSha256,
                        entry.SourceSnapshotHash,
                        standing.RunFenceToken)]),
                CancellationToken.None);

            Assert.True(passed.Succeeded, passed.Error);
        }
    }

    private sealed record Harness(
        FilePlatformStateStore Store,
        FileMigrationRunStore Runs,
        DispositionLedgerService Ledgers,
        PlatformAccessService Platform,
        WorkbenchActor Actor,
        string ProjectId,
        string RunId,
        string WorkspaceId,
        string WorkspaceRoot,
        string SourceSnapshotHash,
        string TargetProfileId,
        int TargetProfileVersion,
        string TargetProfileHash,
        string AnchorPath)
    {
        public string Absolute(string relativePath) =>
            Path.Combine(WorkspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private sealed class SuccessfulBuildGateway : IApplicationBuildGateway
    {
        public Task<ApplicationBuildResult> BuildJavaAsync(string workingDirectory, CancellationToken cancellationToken) =>
            PassAsync("Java", "mvn package");

        public Task<ApplicationBuildResult> BuildReactAsync(string workingDirectory, CancellationToken cancellationToken) =>
            PassAsync("React", "npm run build");

        public Task<ApplicationBuildResult> BuildDotNetAsync(string workingDirectory, CancellationToken cancellationToken) =>
            PassAsync(".NET", "dotnet build");

        private static Task<ApplicationBuildResult> PassAsync(string component, string command) =>
            Task.FromResult(new ApplicationBuildResult(component, command, ToolAvailable: true, ExitCode: 0, "Passed by integration seam."));
    }

    private async Task<Harness> HarnessAsync(bool useActualSnapshot = false)
    {
        FilePlatformStateStore store = new(Path.Combine(_root, "platform-state.json"));
        await store.InitializeAsync(CancellationToken.None);

        FileMigrationRunStore runs = new(Path.Combine(_root, "runs.json"));
        PlatformAccessService platform = new(
            store,
            new ConfiguredSandboxTargetBinding(
                "pg.postgres.database.azure.com", "ofm_sandbox", "id-ofm", CanWrite: true));
        WorkbenchActor actor = WorkbenchActor.ForTenant(Tenant, Operator, [WorkbenchRoles.MigrationOperator]);

        PlatformResult<PlatformProject> project =
            await platform.CreateProjectAsync(actor, "Meridian pilot", CancellationToken.None);
        Assert.True(project.Succeeded, project.Error);
        PlatformResult<PlatformTargetProfile> profile = await platform.EnsureConfiguredTargetProfileAsync(
            actor,
            project.Value!.ProjectId,
            new PlatformTargetProfileEnvironment
            {
                AzureTenantId = Tenant,
                SubscriptionId = "4d1a0e6f-9b77-4b5e-a0ef-2c7d6a41f8b2",
                ResourceGroup = "rg-dev",
                ResourceId = "/subscriptions/4d1a0e6f/resourceGroups/rg-dev/providers/Microsoft.DBforPostgreSQL/flexibleServers/pg",
                Region = "eastus2",
                SchemaName = "public",
                EnvironmentName = "sandbox",
                StackBackEnd = nameof(BackEndStack.AspNetCore),
            },
            CancellationToken.None);
        Assert.True(profile.Succeeded, profile.Error);

        string workspaces = Path.Combine(_root, "workspaces");
        string workspaceId = Guid.NewGuid().ToString("N");
        string workspaceRoot = Path.Combine(workspaces, workspaceId);
        Directory.CreateDirectory(workspaceRoot);

        WorkspaceWriter writer = new(workspaceRoot);
        writer.WriteText($"{SourceRoot}/ui/ORDER_ENTRY.xml", FormsExport);
        writer.WriteText($"{SourceRoot}/db/schema.sql", DotNetPilotFixtures.MeridianSchema);

        MigrationRunRequest request = Request();
        PhasePlan plan = MigrationRunPlanner.Plan(request).Phases
            .Single(phase => phase.Phase == MigrationPhase.SourceNormalization);

        PhaseExecutionResult normalized = await new SourceNormalizationAdapter().ExecuteAsync(
            new PhaseExecutionContext(workspaceRoot, SourceRoot, OutputRoot, plan, request, (_, _) => { }),
            CancellationToken.None);
        Assert.True(normalized.Succeeded, normalized.FailureReason);

        FormsIntermediateRead read = FormsIntermediateReader.Read(writer.ReadText(IrPath, 4L * 1024 * 1024), SourceRoot);
        FormsModule module = read.Modules!.Single();
        FormsSourceFactSet facts = module.SourceFacts!;
        FormsSourceFact block = facts.Facts.First(fact => fact.LocalName == "Block");

        writer.WriteText(ManifestPath, DotNetPilotFixtures.MeridianManifest.Replace(
            "\"sources\": [",
            $$"""
              "sources": [
                          { "id": "frm-block", "kind": "FormsSourceObject", "path": "{{block.Id}}", "module": "{{module.SourcePath}}", "textDigest": "{{facts.TextDigest}}" },
              """,
            StringComparison.Ordinal)
            .Replace("\"sourceRefs\": [\"sch-head\"]", "\"sourceRefs\": [\"sch-head\", \"frm-block\"]", StringComparison.Ordinal));

        SourceWorkspaceService workspaceService = new(workspaces);
        string sourceSnapshotHash = useActualSnapshot
            ? workspaceService.DurableSnapshotHash(workspaceId, SourceRoot)!
            : Snapshot;

        Harness harness = new(
            store,
            runs,
            new DispositionLedgerService(store, platform, runs, workspaceService),
            platform,
            actor,
            project.Value!.ProjectId,
            "run-1",
            workspaceId,
            workspaceRoot,
            sourceSnapshotHash,
            profile.Value!.TargetProfileId,
            profile.Value.Version,
            profile.Value.CanonicalHash,
            block.Id);

        await CompleteRunAsync(harness, "run-1", sourceSnapshotHash, [Artifact(harness, "run-1", IrPath)]);
        return harness;
    }

    private static async Task EnqueueRunAsync(
        Harness harness,
        string runId,
        string snapshotHash,
        string? ledgerId = null,
        string? workspaceNodeId = null)
    {
        await harness.Runs.EnqueueAsync(
            new MigrationRunRecord
            {
                RunId = runId,
                TenantId = Tenant,
                ProjectId = harness.ProjectId,
                ActorObjectId = Operator,
                WorkspaceId = harness.WorkspaceId,
                WorkspaceNodeId = workspaceNodeId ?? "node-a",
                WorkspaceOwnerId = $"{Tenant}:{Operator}/{harness.ProjectId}",
                SourceSnapshotHash = snapshotHash,
                PlanInputHash = new string('b', 64),
                TargetProfileId = harness.TargetProfileId,
                TargetProfileVersion = harness.TargetProfileVersion,
                TargetProfileHash = harness.TargetProfileHash,
                Request = Request() with
                {
                    DispositionLedgerId = ledgerId,
                    Evidence = ledgerId is null
                        ? []
                        : [.. Requests.CompleteEvidence().Where(item =>
                            item.Kind is not (EvidenceKind.FormsModuleInventory or EvidenceKind.BusinessProcessCatalog))],
                },
                EnqueuedUtc = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);
    }

    private static async Task CompleteQueuedRunAsync(
        Harness harness,
        string runId,
        IReadOnlyList<MigrationRunArtifact> artifacts)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        MigrationRunClaim claim = (await harness.Runs.ClaimAsync(
            "node-a", "worker-a", now, TimeSpan.FromMinutes(5), CancellationToken.None))!;
        Assert.Equal(runId, claim.Run.RunId);
        Assert.True(await harness.Runs.MarkRunningAsync(runId, claim.FenceToken, now, CancellationToken.None));
        Assert.True(await harness.Runs.CompleteAsync(
            runId,
            claim.FenceToken,
            MigrationRunState.Succeeded,
            now.AddSeconds(5),
            null,
            null,
            artifacts,
            "done",
            new ProgressSignal(
                ProgressOperations.MigrationRun,
                ProgressActions.RunCompleted,
                ProgressState.Completed,
                "Test run",
                "Terminal",
                "Review"),
            CancellationToken.None));
    }

    private static async Task CompleteRunAsync(
        Harness harness,
        string runId,
        string snapshotHash,
        IReadOnlyList<MigrationRunArtifact> artifacts)
    {
        await EnqueueRunAsync(harness, runId, snapshotHash);
        await CompleteQueuedRunAsync(harness, runId, artifacts);
    }
}
