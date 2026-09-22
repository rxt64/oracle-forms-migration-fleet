using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// The disposition ledger, end to end against durable stores and a real workspace on disk.
///
/// Every case here writes to a temporary file store and reads it back through a freshly constructed store
/// where durability is the property under test. No Azure resource is contacted and no connection is opened.
/// The intermediate representation is the exact shape source normalization writes, because the ledger's
/// whole value is that it enumerates what the source declared rather than what something already understood.
/// </summary>
public sealed class DispositionLedgerTests : IDisposable
{
    private const string Tenant = "8f1e2b42-6a1d-4a24-9ad2-6c1b6a8f3d71";
    private const string OtherTenant = "11111111-2222-3333-4444-555555555555";
    private const string Operator = "3b4c9a10-7d42-4f0e-9d51-2a61f0c4b8e3";
    private const string Bystander = "9c2d7e51-0b83-4a6f-8c19-5d7e2f1a4b60";

    private const string SourceRoot = "legacy/forms";
    private const string IrPath = ".fleet-run/out/intermediate/forms-ir.json";

    /// <summary>The snapshot hash of the acquired source. Deliberately unlike the export's text digest.</summary>
    private const string SnapshotHash = "1111111111111111111111111111111111111111111111111111111111111111";

    private const string NewerSnapshotHash = "2222222222222222222222222222222222222222222222222222222222222222";

    /// <summary>The digest the representation records for the export text. It is not a snapshot identity.</summary>
    private const string TextDigest = "3a7bd3e2360a3d29eea436fcfb7e44c735d117c42d1c1835420b6b9942dd4f1b";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ofm-ledger-{Guid.NewGuid():N}");
    private readonly List<string> _directoryLinks = [];

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
    public async Task Ingest_records_every_declared_property_and_survives_a_restart()
    {
        Harness harness = await HarnessAsync();
        PlatformResult<DispositionLedgerView> ingested = await harness.Ledgers.IngestFromRunAsync(
            harness.Actor, harness.ProjectId, harness.RunId, CancellationToken.None);

        Assert.True(ingested.Succeeded, ingested.Error);
        DispositionLedgerView view = ingested.Value!;

        // One row for each element's existence, each declared attribute, and each direct text node.
        Assert.Equal(22, view.Summary.Counts.Discovered);
        Assert.Equal(22, view.Summary.Counts.Unresolved);
        Assert.Equal(0, view.Summary.Counts.Decided);
        Assert.Equal(0, view.Summary.Counts.Generated);
        Assert.Equal(0, view.Summary.Counts.Verified);
        Assert.Equal(1, view.Summary.ModuleCount);

        DispositionScreenView screen = Assert.Single(view.Screens);
        Assert.Equal("ORDER_ENTRY", screen.ModuleName);
        Assert.Equal("legacy/forms/ui/ORDER_ENTRY.xml", screen.FilePath);
        Assert.Contains(screen.Groups, group => group.BehaviorGroup == "Behaviour: triggers");
        Assert.Contains(screen.Groups, group => group.BehaviorGroup == "Fields");

        // A fresh store instance proves the rows are on disk rather than in a field.
        FilePlatformStateStore restarted = new(harness.StatePath);
        IReadOnlyList<DispositionLedgerEntry> persisted = await restarted.DispositionLedgerEntriesAsync(
            Tenant, view.Summary.LedgerId, CancellationToken.None);

        Assert.Equal(22, persisted.Count);
        Assert.All(persisted, entry => Assert.Equal(SnapshotHash, entry.SourceSnapshotHash));
        Assert.DoesNotContain(persisted, entry => entry.SourceSnapshotHash == TextDigest);
        Assert.All(persisted, entry => Assert.Equal(DispositionDecision.Unresolved, entry.Decision));
        Assert.All(persisted, entry => Assert.Equal(DispositionVerificationStatus.NotExecuted, entry.Verification));

        DispositionLedgerEntry trigger = Assert.Single(persisted, entry =>
            entry.Identity.ObjectType == "Trigger" && entry.Identity.PropertyName == "TriggerText");
        Assert.Equal("BEGIN EXECUTE_QUERY; END;", trigger.ObservedValue);
        Assert.Contains(TextDigest, trigger.ObservedEvidence, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(trigger.Identity.ObjectPath));
    }

    [Fact]
    public async Task A_run_from_another_project_or_another_tenant_is_not_found()
    {
        Harness harness = await HarnessAsync();

        PlatformResult<PlatformProject> second = await harness.Platform.CreateProjectAsync(
            harness.Actor, "Second engagement", CancellationToken.None);
        Assert.True(second.Succeeded, second.Error);

        PlatformResult<DispositionLedgerView> crossProject = await harness.Ledgers.IngestFromRunAsync(
            harness.Actor, second.Value!.ProjectId, harness.RunId, CancellationToken.None);
        Assert.Equal(404, crossProject.Status);

        WorkbenchActor foreign = WorkbenchActor.ForTenant(
            OtherTenant, Operator, [WorkbenchRoles.MigrationOperator, WorkbenchRoles.SandboxApprover]);
        PlatformResult<DispositionLedgerView> crossTenant = await harness.Ledgers.IngestFromRunAsync(
            foreign, harness.ProjectId, harness.RunId, CancellationToken.None);

        // Not 403: the same identifier that is a real project in one tenant must be indistinguishable
        // from a made-up one in another, or project identifiers are enumerable across tenants.
        Assert.Equal(404, crossTenant.Status);
    }

    [Fact]
    public async Task A_project_member_without_the_operator_role_cannot_ingest_or_decide()
    {
        Harness harness = await HarnessAsync();
        await harness.Store.UpsertMembershipAsync(
            new PlatformMembership
            {
                ProjectId = harness.ProjectId,
                TenantId = Tenant,
                ObjectId = Bystander,
                Roles = [WorkbenchRoles.SandboxApprover],
                CreatedUtc = DateTimeOffset.UtcNow,
            },
            expectedVersion: null,
            CancellationToken.None);

        WorkbenchActor approverOnly = WorkbenchActor.ForTenant(Tenant, Bystander, [WorkbenchRoles.SandboxApprover]);
        Assert.Equal(403, (await harness.Ledgers.IngestFromRunAsync(
            approverOnly, harness.ProjectId, harness.RunId, CancellationToken.None)).Status);

        DispositionLedgerView view = await IngestAsync(harness);
        DispositionLedgerEntry entry = await FirstEntryAsync(harness, view.Summary.LedgerId);

        PlatformResult<DispositionLedgerEntry> refused = await harness.Ledgers.DecideAsync(
            approverOnly, view.Summary.LedgerId, entry.EntryId, Preserve(entry.Version), CancellationToken.None);
        Assert.Equal(403, refused.Status);

        // Reading the ledger is open to any member of the project; deciding is not.
        Assert.True((await harness.Ledgers.ViewAsync(approverOnly, view.Summary.LedgerId, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task Edited_intermediate_bytes_are_refused_against_the_run_manifest()
    {
        Harness harness = await HarnessAsync();
        await File.WriteAllTextAsync(harness.IrAbsolutePath, ValidIr.Replace("Order entry", "Order entry ", StringComparison.Ordinal));

        PlatformResult<DispositionLedgerView> result = await harness.Ledgers.IngestFromRunAsync(
            harness.Actor, harness.ProjectId, harness.RunId, CancellationToken.None);

        Assert.Equal(409, result.Status);
        Assert.Contains("no longer matches", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_intermediate_artifact_beneath_a_directory_link_outside_the_workspace_is_not_ingested()
    {
        Harness harness = await HarnessAsync(linkIntermediateOutsideWorkspace: true);

        PlatformResult<DispositionLedgerView> result = await harness.Ledgers.IngestFromRunAsync(
            harness.Actor, harness.ProjectId, harness.RunId, CancellationToken.None);

        Assert.Equal(410, result.Status);
        Assert.Contains("workspace bytes", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_run_yields_one_ledger()
    {
        Harness harness = await HarnessAsync();
        await IngestAsync(harness);

        PlatformResult<DispositionLedgerView> again = await harness.Ledgers.IngestFromRunAsync(
            harness.Actor, harness.ProjectId, harness.RunId, CancellationToken.None);

        Assert.Equal(409, again.Status);
        Assert.Contains("already has a ledger", again.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_properties_sharing_one_identity_refuse_the_whole_ledger()
    {
        FormsSourceFact fact = new(
            "{ns}FormModule[1]", 0, null, 0, "FormModule", "ns", "ORDERS",
            [new FormsSourceAttribute("Name", string.Empty, "ORDERS"), new FormsSourceAttribute("Name", string.Empty, "OTHER")],
            null,
            FormsSourceFactKind.Declared);

        FormsModule module = new(
            "ORDERS", null, [], [], [], [], "legacy/forms/ORDERS.xml",
            new FormsSourceFactSet(TextDigest, null, [fact]));

        (IReadOnlyList<DispositionLedgerEntry>? entries, string? rejection) =
            DispositionLedgerEntries.Project("dled-1", Tenant, "prj-1", SnapshotHash, [module]);

        Assert.Null(entries);
        Assert.Contains("more than once", rejection!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_ledger_or_an_unknown_property_is_refused()
    {
        Harness harness = await HarnessAsync();
        DispositionLedgerView view = await IngestAsync(harness);

        Assert.Equal(404, (await harness.Ledgers.ViewAsync(
            harness.Actor, "dled-does-not-exist", CancellationToken.None)).Status);

        Assert.Equal(404, (await harness.Ledgers.DecideAsync(
            harness.Actor, view.Summary.LedgerId, "not-an-entry", Preserve(1), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_decision_is_refused_without_a_reason_and_a_published_rule()
    {
        Harness harness = await HarnessAsync();
        DispositionLedgerView view = await IngestAsync(harness);
        DispositionLedgerEntry entry = await FirstEntryAsync(harness, view.Summary.LedgerId);

        async Task<int> Decide(DispositionDecision decision, string? rationale, string? ruleId, int ruleVersion) =>
            (await harness.Ledgers.DecideAsync(
                harness.Actor,
                view.Summary.LedgerId,
                entry.EntryId,
                new DispositionDecisionInput(decision, rationale, ruleId, ruleVersion, entry.Version),
                CancellationToken.None)).Status;

        Assert.Equal(400, await Decide(DispositionDecision.Preserve, "  ", "DR-PRESERVE-DECLARED", 1));
        Assert.Equal(400, await Decide(DispositionDecision.Preserve, "Carried across unchanged.", null, 1));
        Assert.Equal(400, await Decide(DispositionDecision.Preserve, "Carried across unchanged.", "DR-MADE-UP", 1));
        Assert.Equal(400, await Decide(DispositionDecision.Preserve, "Carried across unchanged.", "DR-PRESERVE-DECLARED", 99));

        // A published rule that does not authorize this disposition is refused too.
        Assert.Equal(400, await Decide(DispositionDecision.Retire, "Carried across unchanged.", "DR-PRESERVE-DECLARED", 1));
        Assert.Equal(400, await Decide(DispositionDecision.Unresolved, "Carried across unchanged.", "DR-PRESERVE-DECLARED", 1));

        Assert.Equal(200, await Decide(DispositionDecision.Preserve, "Carried across unchanged.", "DR-PRESERVE-DECLARED", 1));
    }

    [Fact]
    public async Task A_decision_read_at_an_older_version_loses_the_race()
    {
        Harness harness = await HarnessAsync();
        DispositionLedgerView view = await IngestAsync(harness);
        DispositionLedgerEntry entry = await FirstEntryAsync(harness, view.Summary.LedgerId);

        PlatformResult<DispositionLedgerEntry> first = await harness.Ledgers.DecideAsync(
            harness.Actor, view.Summary.LedgerId, entry.EntryId, Preserve(entry.Version), CancellationToken.None);
        Assert.True(first.Succeeded, first.Error);
        Assert.Equal(entry.Version + 1, first.Value!.Version);
        Assert.Equal(Operator, first.Value.DecidedByObjectId);

        PlatformResult<DispositionLedgerEntry> stale = await harness.Ledgers.DecideAsync(
            harness.Actor, view.Summary.LedgerId, entry.EntryId, Preserve(entry.Version), CancellationToken.None);
        Assert.Equal(409, stale.Status);
    }

    [Fact]
    public async Task A_newer_source_snapshot_invalidates_the_ledger()
    {
        Harness harness = await HarnessAsync();
        DispositionLedgerView view = await IngestAsync(harness);
        DispositionLedgerEntry entry = await FirstEntryAsync(harness, view.Summary.LedgerId);
        Assert.False(view.Summary.IsStale);

        await CompleteRunAsync(harness, "run-2", NewerSnapshotHash, artifacts: []);

        PlatformResult<DispositionLedgerView> reread = await harness.Ledgers.ViewAsync(
            harness.Actor, view.Summary.LedgerId, CancellationToken.None);
        Assert.True(reread.Succeeded, reread.Error);
        Assert.True(reread.Value!.Summary.IsStale);
        Assert.Contains("superseded", reread.Value.Summary.StaleReason!, StringComparison.Ordinal);
        Assert.False(reread.Value.Summary.Completion.CanComplete);

        PlatformResult<DispositionLedgerEntry> refused = await harness.Ledgers.DecideAsync(
            harness.Actor, view.Summary.LedgerId, entry.EntryId, Preserve(entry.Version), CancellationToken.None);
        Assert.Equal(409, refused.Status);
    }

    [Fact]
    public void A_request_that_names_server_owned_evidence_is_refused()
    {
        static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement.Clone();

        Assert.Null(DispositionLedgerEndpoints.Injected(Body("""{"runId":"run-1"}""")));
        Assert.Null(DispositionLedgerEndpoints.Injected(
            Body("""{"decision":"Preserve","rationale":"Carried across.","mappingRuleId":"DR-PRESERVE-DECLARED","mappingRuleVersion":1,"expectedVersion":1}""")));

        foreach (string injected in new[]
        {
            """{"runId":"run-1","sourceSnapshotHash":"deadbeef"}""",
            """{"decision":"Preserve","verification":"Passed"}""",
            """{"decision":"Preserve","testRefs":[{"testId":"t1","outcome":"Passed"}]}""",
            """{"decision":"Preserve","generatedRefs":[{"artifactPath":"a.java"}]}""",
            """{"decision":"Preserve","decidedByObjectId":"someone-else"}""",
            """{"decision":"Preserve","observedValue":"a different value"}""",
        })
        {
            Assert.NotNull(DispositionLedgerEndpoints.Injected(Body(injected)));
        }
    }

    [Fact]
    public async Task Verification_comes_from_a_test_that_ran_and_never_from_a_generated_file()
    {
        Harness harness = await HarnessAsync();
        DispositionLedgerView view = await IngestAsync(harness);
        DispositionLedgerEntry entry = await FirstEntryAsync(harness, view.Summary.LedgerId);
        string ledgerId = view.Summary.LedgerId;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long fence = await FenceAsync(harness, harness.RunId);
        string content = new('d', 64);

        DispositionGeneratedReference generated = new(
            harness.RunId, "src/main/java/Order.java", content, now, entry.DecisionRevision, fence);

        // Generated and nothing executed stays NotExecuted, however many artifacts exist.
        PlatformResult<DispositionLedgerEntry> written = await harness.Ledgers.RecordVerificationAsync(
            Tenant, ledgerId, new DispositionVerificationEvidence(harness.RunId, entry.EntryId, [generated], []), CancellationToken.None);
        Assert.True(written.Succeeded, written.Error);
        Assert.True(written.Value!.IsGenerated);
        Assert.Equal(DispositionVerificationStatus.NotExecuted, written.Value.Verification);
        Assert.False(written.Value.IsVerified);

        // Evidence from a run that read different source is not evidence about this ledger.
        await CompleteRunAsync(harness, "run-3", NewerSnapshotHash, artifacts: []);
        PlatformResult<DispositionLedgerEntry> foreign = await harness.Ledgers.RecordVerificationAsync(
            Tenant,
            ledgerId,
            new DispositionVerificationEvidence("run-3", entry.EntryId, [],
                [Result("run-3", "t1", DispositionVerificationStatus.Passed, "green", entry, content, fence)]),
            CancellationToken.None);
        Assert.Equal(409, foreign.Status);

        // References belonging to a different run than the evidence declares are refused.
        PlatformResult<DispositionLedgerEntry> mismatched = await harness.Ledgers.RecordVerificationAsync(
            Tenant,
            ledgerId,
            new DispositionVerificationEvidence(harness.RunId, entry.EntryId, [],
                [Result("run-3", "t1", DispositionVerificationStatus.Passed, "green", entry, content, fence)]),
            CancellationToken.None);
        Assert.Equal(400, mismatched.Status);

        PlatformResult<DispositionLedgerEntry> passed = await harness.Ledgers.RecordVerificationAsync(
            Tenant,
            ledgerId,
            new DispositionVerificationEvidence(harness.RunId, entry.EntryId, [],
                [Result(harness.RunId, "OrderEntryTest#account", DispositionVerificationStatus.Passed, "1 assertion", entry, content, fence)]),
            CancellationToken.None);
        Assert.True(passed.Succeeded, passed.Error);
        Assert.Equal(DispositionVerificationStatus.Passed, passed.Value!.Verification);

        PlatformResult<DispositionLedgerEntry> failed = await harness.Ledgers.RecordVerificationAsync(
            Tenant,
            ledgerId,
            new DispositionVerificationEvidence(harness.RunId, entry.EntryId, [],
                [Result(harness.RunId, "OrderEntryTest#prompt", DispositionVerificationStatus.Failed, "expected 'Account'", entry, content, fence)]),
            CancellationToken.None);
        Assert.True(failed.Succeeded, failed.Error);
        Assert.Equal(DispositionVerificationStatus.Failed, failed.Value!.Verification);
    }

    [Fact]
    public async Task A_result_is_refused_while_no_generation_this_run_owns_stands_for_the_property()
    {
        Harness harness = await HarnessAsync();
        DispositionLedgerView view = await IngestAsync(harness);
        DispositionLedgerEntry entry = await FirstEntryAsync(harness, view.Summary.LedgerId);
        long fence = await FenceAsync(harness, harness.RunId);

        // Nothing has been generated for this property, so there is no artifact a result could be about.
        PlatformResult<DispositionLedgerEntry> unbound = await harness.Ledgers.RecordVerificationAsync(
            Tenant,
            view.Summary.LedgerId,
            new DispositionVerificationEvidence(harness.RunId, entry.EntryId, [],
                [Result(harness.RunId, "t1", DispositionVerificationStatus.Passed, "1 assertion", entry, new string('d', 64), fence)]),
            CancellationToken.None);

        Assert.False(unbound.Succeeded);
        Assert.Equal(409, unbound.Status);
        Assert.Contains("not the one this run owns", unbound.Error!, StringComparison.Ordinal);

        DispositionLedgerEntry stored = await FirstEntryAsync(harness, view.Summary.LedgerId);
        Assert.Equal(DispositionVerificationStatus.NotExecuted, stored.Verification);
        Assert.Empty(stored.TestRefs);
    }

    /// <summary>
    /// An artifact a run offers is refused unless it names this property's standing decision, this run's
    /// ownership, some content, and a path that stays inside the workspace the run was confined to.
    /// </summary>
    [Fact]
    public async Task A_generated_reference_is_refused_unless_it_names_the_decision_the_run_and_a_contained_path()
    {
        Harness harness = await HarnessAsync();
        DispositionLedgerView view = await IngestAsync(harness);
        string ledgerId = view.Summary.LedgerId;
        DispositionLedgerEntry entry = await FirstEntryAsync(harness, ledgerId);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long fence = await FenceAsync(harness, harness.RunId);
        string content = new('d', 64);

        async Task<PlatformResult<DispositionLedgerEntry>> OfferAsync(DispositionGeneratedReference reference) =>
            await harness.Ledgers.RecordVerificationAsync(
                Tenant, ledgerId, new DispositionVerificationEvidence(harness.RunId, entry.EntryId, [reference], []),
                CancellationToken.None);

        foreach (string escaping in new[] { "C:/loot/Order.java", "../../../etc/passwd", "/etc/passwd", "out/../../Order.java" })
        {
            PlatformResult<DispositionLedgerEntry> refused = await OfferAsync(
                new(harness.RunId, escaping, content, now, entry.DecisionRevision, fence));
            Assert.Equal(409, refused.Status);
            Assert.Contains("generated artifact path", refused.Error!, StringComparison.OrdinalIgnoreCase);
        }

        PlatformResult<DispositionLedgerEntry> contentless = await OfferAsync(
            new(harness.RunId, "out/Order.java", "   ", now, entry.DecisionRevision, fence));
        Assert.Equal(409, contentless.Status);

        // A row written before this binding existed carries no revision, so it matches none and is refused.
        PlatformResult<DispositionLedgerEntry> unbound = await OfferAsync(
            new(harness.RunId, "out/Order.java", content, now, string.Empty, fence));
        Assert.Equal(409, unbound.Status);
        Assert.Contains("different disposition", unbound.Error!, StringComparison.Ordinal);

        PlatformResult<DispositionLedgerEntry> displaced = await OfferAsync(
            new(harness.RunId, "out/Order.java", content, now, entry.DecisionRevision, fence + 1));
        Assert.Equal(409, displaced.Status);
        Assert.Contains("ownership claim this run no longer holds", displaced.Error!, StringComparison.Ordinal);

        Assert.Empty((await FirstEntryAsync(harness, ledgerId)).GeneratedRefs);
    }

    /// <summary>
    /// A result is admitted against the exact thing it says it ran, and nothing else.
    ///
    /// The producer states what it actually tested — the disposition that stood, the source, the generated
    /// content, and the claim it held — and the server checks that statement rather than supplying it. So
    /// a result about content the same run has since replaced, about a disposition the operator has since
    /// restated for a different reason, or under an ownership claim the run no longer holds, has nothing
    /// to attach to. The identical result arriving twice is the same fact, not a second one.
    /// </summary>
    [Fact]
    public async Task A_late_result_is_admitted_only_against_the_exact_artifact_and_decision_it_names()
    {
        Harness harness = await HarnessAsync();
        DispositionLedgerView view = await IngestAsync(harness);
        string ledgerId = view.Summary.LedgerId;
        DispositionLedgerEntry discovered = await FirstEntryAsync(harness, ledgerId);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long fence = await FenceAsync(harness, harness.RunId);
        const string First = "1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a";
        const string Second = "2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b";

        DispositionLedgerEntry decided = (await harness.Ledgers.DecideAsync(
            harness.Actor, ledgerId, discovered.EntryId, Preserve(discovered.Version), CancellationToken.None)).Value!;

        async Task<PlatformResult<DispositionLedgerEntry>> RecordAsync(
            DispositionLedgerEntry against,
            IReadOnlyList<DispositionGeneratedReference> generated,
            IReadOnlyList<DispositionTestReference> tests) =>
            await harness.Ledgers.RecordVerificationAsync(
                Tenant, ledgerId, new DispositionVerificationEvidence(harness.RunId, against.EntryId, generated, tests),
                CancellationToken.None);

        PlatformResult<DispositionLedgerEntry> proved = await RecordAsync(
            decided,
            [new(harness.RunId, "out/application", First, now, decided.DecisionRevision, fence)],
            [Result(harness.RunId, "GeneratedSurfaceTest#first", DispositionVerificationStatus.Passed, "1 assertion", decided, First, fence)]);
        Assert.True(proved.Succeeded, proved.Error);
        Assert.Equal(DispositionVerificationStatus.Passed, proved.Value!.Verification);

        // The identical artifact and the identical result arriving again are the same facts, not new ones.
        PlatformResult<DispositionLedgerEntry> repeated = await RecordAsync(
            proved.Value,
            [new(harness.RunId, "out/application", First, now, decided.DecisionRevision, fence)],
            [Result(harness.RunId, "GeneratedSurfaceTest#first", DispositionVerificationStatus.Passed, "1 assertion", decided, First, fence)]);
        Assert.True(repeated.Succeeded, repeated.Error);
        Assert.Single(repeated.Value!.GeneratedRefs);
        Assert.Single(repeated.Value.TestRefs);
        Assert.Equal(DispositionVerificationStatus.Passed, repeated.Value.Verification);

        // The same run regenerates different content. What was proved about the output it replaced stops
        // standing, and a result naming that replaced content cannot re-attach itself to what exists now.
        PlatformResult<DispositionLedgerEntry> regenerated = await RecordAsync(
            repeated.Value,
            [new(harness.RunId, "out/application", Second, now, decided.DecisionRevision, fence)],
            []);
        Assert.True(regenerated.Succeeded, regenerated.Error);
        Assert.Equal(DispositionVerificationStatus.NotExecuted, regenerated.Value!.Verification);
        Assert.Equal(2, regenerated.Value.GeneratedRefs.Count);

        PlatformResult<DispositionLedgerEntry> stale = await RecordAsync(
            regenerated.Value,
            [],
            [Result(harness.RunId, "GeneratedSurfaceTest#stale", DispositionVerificationStatus.Passed, "1 assertion", decided, First, fence)]);
        Assert.Equal(409, stale.Status);
        Assert.Contains("different generated content", stale.Error!, StringComparison.Ordinal);

        // A claim the run no longer holds is not the claim the standing artifact was written under.
        PlatformResult<DispositionLedgerEntry> displaced = await RecordAsync(
            regenerated.Value,
            [],
            [Result(harness.RunId, "GeneratedSurfaceTest#displaced", DispositionVerificationStatus.Passed, "1 assertion", decided, Second, fence + 1)]);
        Assert.Equal(409, displaced.Status);

        PlatformResult<DispositionLedgerEntry> retested = await RecordAsync(
            regenerated.Value,
            [],
            [Result(harness.RunId, "GeneratedSurfaceTest#second", DispositionVerificationStatus.Passed, "1 assertion", decided, Second, fence)]);
        Assert.True(retested.Succeeded, retested.Error);
        Assert.Equal(DispositionVerificationStatus.Passed, retested.Value!.Verification);

        // The same disposition restated for a different reason is a different decision, so the artifact
        // generated under the old one stops describing this property and must be generated again.
        DispositionLedgerEntry restated = (await harness.Ledgers.DecideAsync(
            harness.Actor,
            ledgerId,
            decided.EntryId,
            new DispositionDecisionInput(
                DispositionDecision.Preserve,
                "Carried unchanged because the generated control binds the same column.",
                "DR-PRESERVE-DECLARED",
                1,
                retested.Value.Version),
            CancellationToken.None)).Value!;

        Assert.Equal(DispositionVerificationStatus.NotExecuted, restated.Verification);
        Assert.NotEqual(decided.DecisionRevision, restated.DecisionRevision);

        PlatformResult<DispositionEntryPage> page = await harness.Ledgers.EntriesAsync(
            harness.Actor, ledgerId, null, null, false, 0, DispositionLedgerService.MaxEntryPage, CancellationToken.None);
        DispositionLedgerEntry projected = page.Value!.Entries.Single(row => row.EntryId == restated.EntryId);
        Assert.False(projected.IsGenerated);
        Assert.Empty(projected.TestRefs);

        // Generated again under the decision that now stands; a result still naming the old one is refused.
        PlatformResult<DispositionLedgerEntry> reborn = await RecordAsync(
            restated,
            [new(harness.RunId, "out/application", Second, now, restated.DecisionRevision, fence)],
            []);
        Assert.True(reborn.Succeeded, reborn.Error);

        PlatformResult<DispositionLedgerEntry> oldDecision = await RecordAsync(
            reborn.Value!,
            [],
            [Result(harness.RunId, "GeneratedSurfaceTest#old", DispositionVerificationStatus.Passed, "1 assertion", decided, Second, fence)]);
        Assert.Equal(409, oldDecision.Status);
        Assert.Contains("different disposition", oldDecision.Error!, StringComparison.Ordinal);

        PlatformResult<DispositionLedgerEntry> current = await RecordAsync(
            reborn.Value!,
            [],
            [Result(harness.RunId, "GeneratedSurfaceTest#new", DispositionVerificationStatus.Passed, "1 assertion", restated, Second, fence)]);
        Assert.True(current.Succeeded, current.Error);
        Assert.Equal(DispositionVerificationStatus.Passed, current.Value!.Verification);

        // Nothing was deleted along the way: every artifact and every result is still on the row.
        DispositionLedgerEntry audit = await FirstEntryAsync(harness, ledgerId);
        Assert.Equal(3, audit.GeneratedRefs.Count);
        Assert.Equal(3, audit.TestRefs.Count);
    }

    /// <summary>
    /// The row version and the decision are separate facts about a row.
    ///
    /// The server rewrites the row every time it enters an artifact or a result against it, and an
    /// authorization is issued over the decisions rather than over how many times the row was written. A
    /// decision restated for a different reason is a different decision, and the result proved under the
    /// reason that no longer stands stops standing with it.
    /// </summary>
    [Fact]
    public async Task Evidence_leaves_the_decision_revision_where_it_was_and_re_deciding_moves_it()
    {
        Harness harness = await HarnessAsync();
        DispositionLedgerView view = await IngestAsync(harness);
        string ledgerId = view.Summary.LedgerId;
        DispositionLedgerEntry entry = await FirstEntryAsync(harness, ledgerId);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        PlatformResult<DispositionLedgerEntry> decided = await harness.Ledgers.DecideAsync(
            harness.Actor, ledgerId, entry.EntryId, Preserve(entry.Version), CancellationToken.None);
        Assert.True(decided.Succeeded, decided.Error);

        string revision = decided.Value!.DecisionRevision;
        Assert.Equal(64, revision.Length);
        long fence = await FenceAsync(harness, harness.RunId);
        string content = new('d', 64);

        PlatformResult<DispositionLedgerEntry> recorded = await harness.Ledgers.RecordVerificationAsync(
            Tenant,
            ledgerId,
            new DispositionVerificationEvidence(
                harness.RunId,
                entry.EntryId,
                [new DispositionGeneratedReference(harness.RunId, ".fleet-run/out/application", content, now, revision, fence)],
                [Result(harness.RunId, "t1", DispositionVerificationStatus.Passed, "1 assertion", decided.Value, content, fence)]),
            CancellationToken.None);

        Assert.True(recorded.Succeeded, recorded.Error);
        Assert.True(recorded.Value!.Version > decided.Value.Version);
        Assert.Equal(revision, recorded.Value.DecisionRevision);
        Assert.Equal(DispositionVerificationStatus.Passed, recorded.Value.Verification);

        PlatformResult<DispositionLedgerEntry> restated = await harness.Ledgers.DecideAsync(
            harness.Actor,
            ledgerId,
            entry.EntryId,
            new DispositionDecisionInput(
                DispositionDecision.Preserve,
                "Carried unchanged because the generated control binds the same column.",
                "DR-PRESERVE-DECLARED",
                1,
                recorded.Value.Version),
            CancellationToken.None);

        Assert.True(restated.Succeeded, restated.Error);
        Assert.NotEqual(revision, restated.Value!.DecisionRevision);
        Assert.Equal(DispositionVerificationStatus.NotExecuted, restated.Value.Verification);

        // Both the artifact and the result stay as the audit trail; neither is presented or counted any
        // more, because both describe a decision nobody holds now.
        Assert.NotEmpty(restated.Value.TestRefs);
        Assert.True(restated.Value.IsGenerated);

        PlatformResult<DispositionEntryPage> page = await harness.Ledgers.EntriesAsync(
            harness.Actor, ledgerId, null, null, false, 0, DispositionLedgerService.MaxEntryPage, CancellationToken.None);
        DispositionLedgerEntry projected = page.Value!.Entries.Single(row => row.EntryId == entry.EntryId);
        Assert.False(projected.IsGenerated);
        Assert.Empty(projected.TestRefs);
        Assert.Equal(DispositionVerificationStatus.NotExecuted, projected.Verification);
    }

    [Fact]
    public async Task Re_deciding_at_the_same_server_timestamp_does_not_revive_old_evidence()
    {
        DateTimeOffset frozen = DateTimeOffset.Parse("2026-09-22T12:00:00Z");
        Harness harness = await HarnessAsync(() => frozen);
        DispositionLedgerView view = await IngestAsync(harness);
        string ledgerId = view.Summary.LedgerId;
        DispositionLedgerEntry entry = await FirstEntryAsync(harness, ledgerId);

        DispositionLedgerEntry decided = (await harness.Ledgers.DecideAsync(
            harness.Actor, ledgerId, entry.EntryId, Preserve(entry.Version), CancellationToken.None)).Value!;
        long fence = await FenceAsync(harness, harness.RunId);
        string content = new('d', 64);

        DispositionLedgerEntry verified = (await harness.Ledgers.RecordVerificationAsync(
            Tenant,
            ledgerId,
            new DispositionVerificationEvidence(
                harness.RunId,
                entry.EntryId,
                [new DispositionGeneratedReference(
                    harness.RunId, ".fleet-run/out/application", content, frozen, decided.DecisionRevision, fence)],
                [Result(harness.RunId, "t1", DispositionVerificationStatus.Passed, "1 assertion", decided, content, fence)]),
            CancellationToken.None)).Value!;

        DispositionLedgerEntry restated = (await harness.Ledgers.DecideAsync(
            harness.Actor, ledgerId, entry.EntryId, Preserve(verified.Version), CancellationToken.None)).Value!;

        Assert.Equal(DispositionVerificationStatus.NotExecuted, restated.Verification);
        Assert.NotEqual(decided.DecisionRevision, restated.DecisionRevision);

        DispositionLedgerEntry projected = Assert.Single((await harness.Ledgers.EntriesAsync(
            harness.Actor, ledgerId, null, null, false, 0, DispositionLedgerService.MaxEntryPage,
            CancellationToken.None)).Value!.Entries, candidate => candidate.EntryId == entry.EntryId);
        Assert.False(projected.IsGenerated);
        Assert.Empty(projected.TestRefs);
    }

    [Fact]
    public async Task Persisted_evidence_without_binding_fields_reads_fail_closed()
    {
        Harness harness = await HarnessAsync();
        DispositionLedgerView view = await IngestAsync(harness);
        string ledgerId = view.Summary.LedgerId;
        DispositionLedgerEntry entry = await FirstEntryAsync(harness, ledgerId);
        DispositionLedgerEntry decided = (await harness.Ledgers.DecideAsync(
            harness.Actor, ledgerId, entry.EntryId, Preserve(entry.Version), CancellationToken.None)).Value!;
        long fence = await FenceAsync(harness, harness.RunId);
        string content = new('e', 64);

        PlatformResult<DispositionLedgerEntry> recorded = await harness.Ledgers.RecordVerificationAsync(
            Tenant,
            ledgerId,
            new DispositionVerificationEvidence(
                harness.RunId,
                entry.EntryId,
                [new DispositionGeneratedReference(
                    harness.RunId, ".fleet-run/out/application", content, DateTimeOffset.UtcNow,
                    decided.DecisionRevision, fence)],
                [Result(harness.RunId, "t1", DispositionVerificationStatus.Passed, "1 assertion", decided, content, fence)]),
            CancellationToken.None);
        Assert.True(recorded.Succeeded, recorded.Error);

        JsonNode document = JsonNode.Parse(await File.ReadAllTextAsync(harness.StatePath))!;
        JsonObject persisted = document["dispositionLedgerEntries"]!.AsArray()
            .Select(node => node!.AsObject())
            .Single(candidate => candidate["entryId"]!.GetValue<string>() == entry.EntryId);
        JsonObject generated = persisted["generatedRefs"]!.AsArray().Single()!.AsObject();
        generated.Remove("decisionRevision");
        generated.Remove("runFenceToken");
        JsonObject test = persisted["testRefs"]!.AsArray().Single()!.AsObject();
        test.Remove("decisionRevision");
        test.Remove("generatedContentSha256");
        test.Remove("sourceSnapshotHash");
        test.Remove("producerFenceToken");
        await File.WriteAllTextAsync(harness.StatePath, document.ToJsonString());

        FilePlatformStateStore restarted = new(harness.StatePath);
        DispositionLedgerService service = new(restarted, harness.Platform, harness.Runs,
            new SourceWorkspaceService(Path.Combine(_root, "workspaces")));

        PlatformResult<DispositionEntryPage> page = await service.EntriesAsync(
            harness.Actor, ledgerId, null, null, false, 0, DispositionLedgerService.MaxEntryPage,
            CancellationToken.None);

        Assert.True(page.Succeeded, page.Error);
        DispositionLedgerEntry projected = Assert.Single(
            page.Value!.Entries, candidate => candidate.EntryId == entry.EntryId);
        Assert.False(projected.IsGenerated);
        Assert.Empty(projected.TestRefs);
        Assert.Equal(DispositionVerificationStatus.NotExecuted, projected.Verification);
    }

    [Fact]
    public async Task Counts_keep_decisions_generation_and_verification_apart()
    {
        Harness harness = await HarnessAsync();
        DispositionLedgerView view = await IngestAsync(harness);
        string ledgerId = view.Summary.LedgerId;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long fence = await FenceAsync(harness, harness.RunId);

        IReadOnlyList<DispositionLedgerEntry> entries =
            await harness.Store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None);

        foreach (DispositionLedgerEntry entry in entries)
        {
            PlatformResult<DispositionLedgerEntry> decided = await harness.Ledgers.DecideAsync(
                harness.Actor, ledgerId, entry.EntryId, Preserve(entry.Version), CancellationToken.None);
            Assert.True(decided.Succeeded, decided.Error);
        }

        DispositionLedgerSummary afterDecisions = (await harness.Ledgers.ViewAsync(
            harness.Actor, ledgerId, CancellationToken.None)).Value!.Summary;

        Assert.Equal(22, afterDecisions.Counts.Decided);
        Assert.Equal(0, afterDecisions.Counts.Generated);
        Assert.Equal(0, afterDecisions.Counts.Verified);
        Assert.False(afterDecisions.Completion.CanComplete);
        Assert.Contains(afterDecisions.Completion.Blockers, blocker => blocker.Contains("generated artifact", StringComparison.Ordinal));

        foreach (DispositionLedgerEntry entry in
            await harness.Store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None))
        {
            string content = new('d', 64);
            PlatformResult<DispositionLedgerEntry> recorded = await harness.Ledgers.RecordVerificationAsync(
                Tenant,
                ledgerId,
                new DispositionVerificationEvidence(
                    harness.RunId,
                    entry.EntryId,
                    [new DispositionGeneratedReference(
                        harness.RunId, "src/main/java/Order.java", content, now, entry.DecisionRevision, fence)],
                    [Result(
                        harness.RunId, $"T#{entry.EntryId[..8]}", DispositionVerificationStatus.Passed, "1 assertion",
                        entry, content, fence)]),
                CancellationToken.None);
            Assert.True(recorded.Succeeded, recorded.Error);
        }

        DispositionLedgerSummary complete = (await harness.Ledgers.ViewAsync(
            harness.Actor, ledgerId, CancellationToken.None)).Value!.Summary;

        Assert.Equal(22, complete.Counts.Generated);
        Assert.Equal(22, complete.Counts.Verified);
        Assert.True(complete.Completion.CanComplete);
        Assert.Empty(complete.Completion.Blockers);
    }

    [Fact]
    public void Anything_unresolved_or_deferred_blocks_completion()
    {
        DispositionCompletionState unresolved = DispositionLedgerRules.Completion(
            new DispositionLedgerCounts(10, 9, 9, 9, 1, 0, 0));
        Assert.False(unresolved.CanComplete);
        Assert.Contains(unresolved.Blockers, blocker => blocker.Contains("no recorded disposition", StringComparison.Ordinal));

        DispositionCompletionState deferred = DispositionLedgerRules.Completion(
            new DispositionLedgerCounts(10, 10, 10, 10, 0, 1, 0));
        Assert.False(deferred.CanComplete);
        Assert.Contains(deferred.Blockers, blocker => blocker.Contains("deferred", StringComparison.Ordinal));

        DispositionCompletionState failing = DispositionLedgerRules.Completion(
            new DispositionLedgerCounts(10, 10, 10, 10, 0, 0, 1));
        Assert.False(failing.CanComplete);

        Assert.False(DispositionLedgerRules.Completion(new DispositionLedgerCounts(0, 0, 0, 0, 0, 0, 0)).CanComplete);
        Assert.True(DispositionLedgerRules.Completion(new DispositionLedgerCounts(10, 10, 10, 10, 0, 0, 0)).CanComplete);
    }

    [Fact]
    public async Task Entry_pages_are_bounded_and_filter_by_screen_and_behaviour()
    {
        Harness harness = await HarnessAsync();
        DispositionLedgerView view = await IngestAsync(harness);

        PlatformResult<DispositionEntryPage> page = await harness.Ledgers.EntriesAsync(
            harness.Actor, view.Summary.LedgerId, "ORDER_ENTRY", "Fields",
            undecidedOnly: true, skip: 0, take: 5_000, CancellationToken.None);

        Assert.True(page.Succeeded, page.Error);
        Assert.Equal(8, page.Value!.Total);
        Assert.Equal(DispositionLedgerService.MaxEntryPage, page.Value.Take);
        Assert.All(page.Value.Entries, entry => Assert.Equal("Fields", entry.Identity.BehaviorGroup));

        PlatformResult<DispositionEntryPage> none = await harness.Ledgers.EntriesAsync(
            harness.Actor, view.Summary.LedgerId, "NO_SUCH_MODULE", null,
            undecidedOnly: false, skip: 0, take: 50, CancellationToken.None);
        Assert.Equal(0, none.Value!.Total);
    }

    private static DispositionDecisionInput Preserve(int expectedVersion) => new(
        DispositionDecision.Preserve,
        "The declared value is carried into the generated application unchanged.",
        "DR-PRESERVE-DECLARED",
        1,
        expectedVersion);

    private static async Task<DispositionLedgerView> IngestAsync(Harness harness)
    {
        PlatformResult<DispositionLedgerView> ingested = await harness.Ledgers.IngestFromRunAsync(
            harness.Actor, harness.ProjectId, harness.RunId, CancellationToken.None);
        Assert.True(ingested.Succeeded, ingested.Error);
        return ingested.Value!;
    }

    private static async Task<DispositionLedgerEntry> FirstEntryAsync(Harness harness, string ledgerId) =>
        (await harness.Store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None))[0];

    private static async Task<long> FenceAsync(Harness harness, string runId) =>
        (await harness.Runs.GetAsync(Tenant, runId, CancellationToken.None))!.FenceToken;

    /// <summary>
    /// A result as a producing worker states it: bound to the decision that stood, the source it read,
    /// the content it ran against, and the claim it held while it ran.
    /// </summary>
    private static DispositionTestReference Result(
        string runId,
        string testId,
        DispositionVerificationStatus outcome,
        string detail,
        DispositionLedgerEntry against,
        string contentSha256,
        long producerFenceToken) => new(
            runId,
            testId,
            outcome,
            detail,
            DateTimeOffset.UtcNow,
            against.DecisionRevision,
            contentSha256,
            against.SourceSnapshotHash,
            producerFenceToken);

    private sealed record Harness(
        string StatePath,
        FilePlatformStateStore Store,
        FileMigrationRunStore Runs,
        PlatformAccessService Platform,
        DispositionLedgerService Ledgers,
        WorkbenchActor Actor,
        string ProjectId,
        string RunId,
        string WorkspaceId,
        string IrAbsolutePath);

    /// <summary>
    /// A project, a workspace on disk holding the representation a run wrote, and that run recorded as
    /// succeeded with its own artifact manifest. This is the shape every ledger read starts from.
    /// </summary>
    private async Task<Harness> HarnessAsync(
        Func<DateTimeOffset>? clock = null,
        bool linkIntermediateOutsideWorkspace = false)
    {
        string statePath = Path.Combine(_root, "platform-state.json");
        FilePlatformStateStore store = new(statePath);
        await store.InitializeAsync(CancellationToken.None);

        FileMigrationRunStore runs = new(Path.Combine(_root, "runs.json"));
        PlatformAccessService platform = new(store, sandbox: null);
        WorkbenchActor actor = WorkbenchActor.ForTenant(
            Tenant, Operator, [WorkbenchRoles.MigrationOperator, WorkbenchRoles.SandboxApprover]);

        PlatformResult<PlatformProject> project =
            await platform.CreateProjectAsync(actor, "ORDER_ENTRY migration", CancellationToken.None);
        Assert.True(project.Succeeded, project.Error);

        string workspaceRootDirectory = Path.Combine(_root, "workspaces");
        SourceWorkspaceService workspaces = new(workspaceRootDirectory);
        string workspaceId = Guid.NewGuid().ToString("N");
        string absolute = Path.Combine(
            workspaceRootDirectory, workspaceId, IrPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllTextAsync(absolute, ValidIr);

        if (linkIntermediateOutsideWorkspace)
        {
            string linkedDirectory = Path.GetDirectoryName(absolute)!;
            string externalDirectory = Path.Combine(_root, "known-junction-target-ingest");
            Directory.CreateDirectory(externalDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(externalDirectory, Path.GetFileName(absolute)),
                ValidIr.Replace("Order entry", "JUNCTION-01", StringComparison.Ordinal));
            Directory.Delete(linkedDirectory, recursive: true);
            CreateDirectoryLink(linkedDirectory, externalDirectory);
        }

        Harness harness = new(
            statePath,
            store,
            runs,
            platform,
            new DispositionLedgerService(store, platform, runs, workspaces, clock),
            actor,
            project.Value!.ProjectId,
            "run-1",
            workspaceId,
            absolute);

        byte[] bytes = await File.ReadAllBytesAsync(absolute);
        await CompleteRunAsync(
            harness,
            "run-1",
            SnapshotHash,
            [new MigrationRunArtifact(
                "run-1", IrPath, "NormalizedSource", "Normalized Forms representation",
                bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))]);

        return harness;
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

    private static async Task CompleteRunAsync(
        Harness harness,
        string runId,
        string snapshotHash,
        IReadOnlyList<MigrationRunArtifact> artifacts)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await harness.Runs.EnqueueAsync(
            new MigrationRunRecord
            {
                RunId = runId,
                TenantId = Tenant,
                ProjectId = harness.ProjectId,
                ActorObjectId = Operator,
                WorkspaceId = harness.WorkspaceId,
                WorkspaceNodeId = "node-a",
                WorkspaceOwnerId = $"{Tenant}:{Operator}/{harness.ProjectId}",
                SourceSnapshotHash = snapshotHash,
                PlanInputHash = new string('b', 64),
                TargetProfileId = "sandbox",
                TargetProfileVersion = 1,
                TargetProfileHash = new string('c', 64),
                Request = new MigrationRunRequest
                {
                    EngagementId = "ENG-1",
                    ApplicationName = "ORDER_ENTRY",
                    RequestedMode = ExecutionMode.PlanOnly,
                    Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
                    SourceRoot = SourceRoot,
                    OutputRoot = ".fleet-run/out",
                },
                EnqueuedUtc = now,
            },
            CancellationToken.None);

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

    /// <summary>
    /// A representation in the exact shape source normalization writes, carrying one module, one block,
    /// one item, two triggers, and the retained fact inventory those were interpreted from.
    /// </summary>
    private const string ValidIr = """
        {
          "generator": "oracle-forms-migration-fleet/source-normalization",
          "schemaVersion": "3",
          "normalized": true,
          "sourceRoot": "legacy/forms",
          "formsFamily": "12c",
          "versionAuthority": "declared by the supplied export",
          "modules": [
            {
              "name": "ORDER_ENTRY",
              "title": "Order entry",
              "sourcePath": "legacy/forms/ui/ORDER_ENTRY.xml",
              "declaredVersion": "12.2.1.4",
              "declaredFamily": "12c",
              "blocks": [
                {
                  "name": "ORDER_BLOCK",
                  "baseTable": "BANK_ACCOUNT",
                  "recordsDisplayed": 10,
                  "items": [
                    {
                      "name": "ACCOUNT_ID",
                      "itemType": "Text Item",
                      "dataType": "Number",
                      "columnName": "ACCOUNT_ID",
                      "prompt": "Account",
                      "maxLength": 12,
                      "required": true,
                      "visible": true
                    }
                  ],
                  "triggers": [{"name": "WHEN-VALIDATE-ITEM","scope": "ORDER_BLOCK.ACCOUNT_ID","body": "BEGIN VALIDATE_ITEM; END;","bodyEncoding": "Element"}]
                }
              ],
              "triggers": [{"name": "WHEN-NEW-FORM-INSTANCE","scope": "ORDER_ENTRY","body": "BEGIN EXECUTE_QUERY; END;","bodyEncoding": "Attribute"}],
              "programUnits": [],
              "lovs": [],
              "sourceFacts": {
                "textDigest": "3a7bd3e2360a3d29eea436fcfb7e44c735d117c42d1c1835420b6b9942dd4f1b",
                "wrapperDeclaredVersion": "12.2.1.4",
                "facts": [
                  {
                    "id": "{http://xmlns.oracle.com/Forms}FormModule[1]",
                    "order": 0,
                    "childIndex": 0,
                    "localName": "FormModule",
                    "namespace": "http://xmlns.oracle.com/Forms",
                    "declaredName": "ORDER_ENTRY",
                    "attributes": [
                      {"name": "Name", "namespace": "", "value": "ORDER_ENTRY"},
                      {"name": "Title", "namespace": "", "value": "Order entry"}
                    ],
                    "kind": "Declared"
                  },
                  {
                    "id": "{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Trigger[1]",
                    "order": 1,
                    "parentId": "{http://xmlns.oracle.com/Forms}FormModule[1]",
                    "childIndex": 0,
                    "localName": "Trigger",
                    "namespace": "http://xmlns.oracle.com/Forms",
                    "declaredName": "WHEN-NEW-FORM-INSTANCE",
                    "attributes": [
                      {"name": "Name", "namespace": "", "value": "WHEN-NEW-FORM-INSTANCE"},
                      {"name": "TriggerText", "namespace": "", "value": "BEGIN EXECUTE_QUERY; END;"}
                    ],
                    "kind": "Declared"
                  },
                  {
                    "id": "{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]",
                    "order": 2,
                    "parentId": "{http://xmlns.oracle.com/Forms}FormModule[1]",
                    "childIndex": 1,
                    "localName": "Block",
                    "namespace": "http://xmlns.oracle.com/Forms",
                    "declaredName": "ORDER_BLOCK",
                    "attributes": [
                      {"name": "Name", "namespace": "", "value": "ORDER_BLOCK"},
                      {"name": "QueryDataSourceName", "namespace": "", "value": "BANK_ACCOUNT"},
                      {"name": "RecordsDisplayCount", "namespace": "", "value": "10"}
                    ],
                    "kind": "Declared"
                  },
                  {
                    "id": "{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]/{http://xmlns.oracle.com/Forms}Item[1]",
                    "order": 3,
                    "parentId": "{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]",
                    "childIndex": 0,
                    "localName": "Item",
                    "namespace": "http://xmlns.oracle.com/Forms",
                    "declaredName": "ACCOUNT_ID",
                    "attributes": [
                      {"name": "Name", "namespace": "", "value": "ACCOUNT_ID"},
                      {"name": "ItemType", "namespace": "", "value": "Text Item"},
                      {"name": "DataType", "namespace": "", "value": "Number"},
                      {"name": "Prompt", "namespace": "", "value": "Account:"},
                      {"name": "Required", "namespace": "", "value": "Yes"},
                      {"name": "MaximumLength", "namespace": "", "value": "12"},
                      {"name": "FormatMask", "namespace": "", "value": "999G999"}
                    ],
                    "kind": "Declared"
                  },
                  {
                    "id": "{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]/{http://xmlns.oracle.com/Forms}Item[1]/{http://xmlns.oracle.com/Forms}Trigger[1]",
                    "order": 4,
                    "parentId": "{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]/{http://xmlns.oracle.com/Forms}Item[1]",
                    "childIndex": 0,
                    "localName": "Trigger",
                    "namespace": "http://xmlns.oracle.com/Forms",
                    "declaredName": "WHEN-VALIDATE-ITEM",
                    "attributes": [
                      {"name": "Name", "namespace": "", "value": "WHEN-VALIDATE-ITEM"}
                    ],
                    "kind": "Declared"
                  },
                  {
                    "id": "{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]/{http://xmlns.oracle.com/Forms}Item[1]/{http://xmlns.oracle.com/Forms}Trigger[1]/{http://xmlns.oracle.com/Forms}TriggerText[1]",
                    "order": 5,
                    "parentId": "{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]/{http://xmlns.oracle.com/Forms}Item[1]/{http://xmlns.oracle.com/Forms}Trigger[1]",
                    "childIndex": 0,
                    "localName": "TriggerText",
                    "namespace": "http://xmlns.oracle.com/Forms",
                    "attributes": [],
                    "text": "BEGIN VALIDATE_ITEM; END;",
                    "kind": "Declared"
                  }
                ]
              }
            }
          ],
          "notes": []
        }
        """;
}
