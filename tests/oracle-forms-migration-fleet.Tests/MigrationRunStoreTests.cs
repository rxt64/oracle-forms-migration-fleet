// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

public sealed class MigrationRunStoreTests : IDisposable
{
    private const string Tenant = "8f1e2b42-6a1d-4a24-9ad2-6c1b6a8f3d71";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ofm-runs-{Guid.NewGuid():N}");
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Expired_lease_takeover_increments_the_fence_and_rejects_the_stale_worker()
    {
        FileMigrationRunStore store = Store();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await store.EnqueueAsync(Run(now), CancellationToken.None);
        MigrationRunClaim first = (await store.ClaimAsync(
            "node-a", "worker-a", now, TimeSpan.FromMinutes(1), CancellationToken.None))!;
        Assert.Null(await store.ClaimAsync(
            "node-a", "worker-b", now.AddSeconds(30), TimeSpan.FromMinutes(1), CancellationToken.None));
        MigrationRunClaim second = (await store.ClaimAsync(
            "node-a", "worker-b", now.AddMinutes(2), TimeSpan.FromMinutes(1), CancellationToken.None))!;
        Assert.Equal(first.FenceToken + 1, second.FenceToken);
        Assert.Null(await store.AppendEventAsync(
            first.Run.RunId, first.FenceToken, now, "info", "stale", null, null, CancellationToken.None));
        Assert.NotNull(await store.AppendEventAsync(
            second.Run.RunId, second.FenceToken, now, "info", "current", null, null, CancellationToken.None));
        Assert.False(await store.CompleteAsync(
            first.Run.RunId,
            first.FenceToken,
            MigrationRunState.Failed,
            now,
            null,
            "stale",
            [],
            "error",
            TerminalSignal(ProgressState.Failed),
            CancellationToken.None));
    }

    [Fact]
    public async Task Events_replay_after_a_sequence_without_gaps_or_duplicates()
    {
        FileMigrationRunStore store = Store();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await store.EnqueueAsync(Run(now), CancellationToken.None);
        MigrationRunClaim claim = (await store.ClaimAsync(
            "node-a", "worker-a", now, TimeSpan.FromMinutes(1), CancellationToken.None))!;
        for (int index = 1; index <= 3; index++)
        {
            MigrationRunEvent appended = (await store.AppendEventAsync(
                claim.Run.RunId,
                claim.FenceToken,
                now.AddSeconds(index),
                "info",
                $"event-{index}",
                null,
                null,
                CancellationToken.None))!;
            Assert.Equal(index, appended.Sequence);
        }
        IReadOnlyList<MigrationRunEvent> replay = await store.EventsAsync(
            Tenant, claim.Run.RunId, afterSequence: 1, CancellationToken.None);
        Assert.Equal([2L, 3L], replay.Select(item => item.Sequence));
        Assert.Equal(["event-2", "event-3"], replay.Select(item => item.Text));
        Assert.Empty(await store.EventsAsync(
            "11111111-2222-3333-4444-555555555555", claim.Run.RunId, 0, CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_and_terminal_artifact_manifest_survive_a_restart()
    {
        FileMigrationRunStore store = Store();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MigrationRunRecord run = await store.EnqueueAsync(Run(now), CancellationToken.None);
        MigrationRunClaim claim = (await store.ClaimAsync(
            "node-a", "worker-a", now, TimeSpan.FromMinutes(1), CancellationToken.None))!;
        Assert.True(await store.MarkRunningAsync(run.RunId, claim.FenceToken, now, CancellationToken.None));
        Assert.True(await store.RequestCancellationAsync(
            Tenant, run.RunId, "operator", now.AddSeconds(1), CancellationToken.None));
        Assert.True(await store.CompleteAsync(
            run.RunId,
            claim.FenceToken,
            MigrationRunState.Cancelled,
            now.AddSeconds(30),
            null,
            "Cancellation was observed at a safe point.",
            [new MigrationRunArtifact(run.RunId, ".fleet-run/runs/run-1/report.md", "Report", "Run report", 12, new string('a', 64))],
            "error",
            TerminalSignal(ProgressState.Failed),
            CancellationToken.None));
        FileMigrationRunStore restarted = Store();
        MigrationRunRecord persisted = (await restarted.GetAsync(Tenant, run.RunId, CancellationToken.None))!;
        Assert.Equal(MigrationRunState.Cancelled, persisted.State);
        Assert.Equal("operator", persisted.CancelRequestedByObjectId);
        Assert.Single(await restarted.ArtifactsAsync(Tenant, run.RunId, CancellationToken.None));
        Assert.False(await restarted.RequestCancellationAsync(
            Tenant, run.RunId, "operator", now, CancellationToken.None));
        Assert.Null(await restarted.AppendEventAsync(
            run.RunId, claim.FenceToken, now, "info", "late", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Lease_takeover_fences_the_old_worker_before_its_next_external_call()
    {
        FileMigrationRunStore store = Store();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MigrationRunRecord run = await store.EnqueueAsync(Run(now), CancellationToken.None);
        MigrationRunClaim first = (await store.ClaimAsync(
            "node-a", "worker-a", now, TimeSpan.FromSeconds(10), CancellationToken.None))!;
        FencedDataMigrationGateway gateway = new(
            new StubDataGateway(new DataMigrationOutcome(0, 0, [], [])),
            store,
            run.RunId,
            first.FenceToken,
            TimeSpan.FromSeconds(10));

        Assert.Empty(await gateway.CountAsync([], CancellationToken.None));
        Assert.NotNull(await store.ClaimAsync(
            "node-a", "worker-b", now.AddMinutes(1), TimeSpan.FromSeconds(10), CancellationToken.None));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => gateway.CountAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task Started_runs_are_never_taken_over_after_lease_expiry()
    {
        FileMigrationRunStore store = Store();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MigrationRunRecord run = await store.EnqueueAsync(Run(now), CancellationToken.None);
        MigrationRunClaim claim = (await store.ClaimAsync(
            "node-a", "worker-a", now, TimeSpan.FromSeconds(10), CancellationToken.None))!;
        Assert.True(await store.MarkRunningAsync(run.RunId, claim.FenceToken, now, CancellationToken.None));

        Assert.Null(await store.ClaimAsync(
            "node-a", "worker-b", now.AddMinutes(1), TimeSpan.FromSeconds(10), CancellationToken.None));
    }

    [Fact]
    public async Task Same_node_restart_fences_and_interrupts_an_expired_started_run()
    {
        FileMigrationRunStore store = Store();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MigrationRunRecord run = await store.EnqueueAsync(Run(now), CancellationToken.None);
        MigrationRunClaim claim = (await store.ClaimAsync(
            "node-a", "worker-a", now, TimeSpan.FromSeconds(10), CancellationToken.None))!;
        Assert.True(await store.MarkRunningAsync(run.RunId, claim.FenceToken, now, CancellationToken.None));

        Assert.Equal(1, await store.ReconcileExpiredAsync(
            "node-a", now.AddMinutes(1), "Process restarted.", CancellationToken.None));
        MigrationRunRecord interrupted = (await store.GetAsync(Tenant, run.RunId, CancellationToken.None))!;
        Assert.Equal(MigrationRunState.Interrupted, interrupted.State);
        Assert.Equal(claim.FenceToken + 1, interrupted.FenceToken);
        MigrationRunEvent terminal = Assert.Single(await store.EventsAsync(Tenant, run.RunId, 0, CancellationToken.None));
        Assert.Equal("Process restarted.", terminal.Text);
        Assert.Null(await store.AppendEventAsync(
            run.RunId, claim.FenceToken, now, "info", "late", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Replacement_node_interrupts_an_expired_pre_start_lease_it_cannot_execute()
    {
        FileMigrationRunStore store = Store();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MigrationRunRecord run = await store.EnqueueAsync(Run(now), CancellationToken.None);
        await store.ClaimAsync("node-a", "worker-a", now, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(1, await store.ReconcileExpiredAsync(
            "node-b", now.AddMinutes(1), "The source-owning replica stopped.", CancellationToken.None));
        Assert.Equal(
            MigrationRunState.Interrupted,
            (await store.GetAsync(Tenant, run.RunId, CancellationToken.None))!.State);
        Assert.Null(await store.ClaimAsync(
            "node-b", "worker-b", now.AddMinutes(1), TimeSpan.FromSeconds(10), CancellationToken.None));
    }

    [Fact]
    public async Task Expired_lease_rejects_start_events_completion_and_external_calls()
    {
        FileMigrationRunStore store = Store();
        DateTimeOffset past = DateTimeOffset.UtcNow.AddMinutes(-2);
        MigrationRunRecord run = await store.EnqueueAsync(Run(past), CancellationToken.None);
        MigrationRunClaim claim = (await store.ClaimAsync(
            "node-a", "worker-a", past, TimeSpan.FromSeconds(10), CancellationToken.None))!;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Assert.False(await store.MarkRunningAsync(run.RunId, claim.FenceToken, now, CancellationToken.None));
        Assert.Null(await store.AppendEventAsync(
            run.RunId, claim.FenceToken, now, "info", "late", null, null, CancellationToken.None));
        Assert.False(await store.CompleteAsync(
            run.RunId,
            claim.FenceToken,
            MigrationRunState.Failed,
            now,
            null,
            "late",
            [],
            "error",
            TerminalSignal(ProgressState.Failed),
            CancellationToken.None));
        FencedDataMigrationGateway gateway = new(
            new StubDataGateway(new DataMigrationOutcome(0, 0, [], [])),
            store,
            run.RunId,
            claim.FenceToken,
            TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => gateway.CountAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_persisted_after_claim_atomically_refuses_start()
    {
        FileMigrationRunStore store = Store();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MigrationRunRecord run = await store.EnqueueAsync(Run(now), CancellationToken.None);
        MigrationRunClaim claim = (await store.ClaimAsync(
            "node-a", "worker-a", now, TimeSpan.FromMinutes(1), CancellationToken.None))!;
        Assert.True(await store.RequestCancellationAsync(
            Tenant, run.RunId, "operator", now.AddSeconds(1), CancellationToken.None));

        Assert.False(await store.MarkRunningAsync(
            run.RunId, claim.FenceToken, now.AddSeconds(2), CancellationToken.None));
        Assert.Equal(MigrationRunState.Leased, (await store.GetAsync(Tenant, run.RunId, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task Accepted_cancellation_atomically_beats_successful_completion()
    {
        FileMigrationRunStore store = Store();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MigrationRunRecord run = await store.EnqueueAsync(Run(now), CancellationToken.None);
        MigrationRunClaim claim = (await store.ClaimAsync(
            "node-a", "worker-a", now, TimeSpan.FromMinutes(1), CancellationToken.None))!;
        Assert.True(await store.MarkRunningAsync(run.RunId, claim.FenceToken, now, CancellationToken.None));
        Assert.True(await store.RequestCancellationAsync(
            Tenant, run.RunId, "operator", now.AddSeconds(1), CancellationToken.None));

        Assert.False(await store.CompleteAsync(
            run.RunId, claim.FenceToken, MigrationRunState.Succeeded, now.AddSeconds(2), null, null, [],
            "done", TerminalSignal(ProgressState.Completed), CancellationToken.None));
        Assert.True(await store.CompleteAsync(
            run.RunId, claim.FenceToken, MigrationRunState.Cancelled, now.AddSeconds(2), null, "Cancelled", [],
            "error", TerminalSignal(ProgressState.Failed), CancellationToken.None));
        Assert.Equal(MigrationRunState.Cancelled, (await store.GetAsync(Tenant, run.RunId, CancellationToken.None))!.State);
        Assert.Single(await store.EventsAsync(Tenant, run.RunId, 0, CancellationToken.None));
    }

    private FileMigrationRunStore Store() => new(Path.Combine(_root, "runs.json"));

    private static ProgressSignal TerminalSignal(ProgressState state) => new(
        ProgressOperations.MigrationRun,
        state == ProgressState.Completed ? ProgressActions.RunCompleted : ProgressActions.RunFailed,
        state,
        "Test run",
        "Terminal",
        "Review");

    private static MigrationRunRecord Run(DateTimeOffset enqueuedUtc) => new()
    {
        RunId = "run-1",
        TenantId = Tenant,
        ProjectId = "prj-1",
        ActorObjectId = "operator",
        WorkspaceId = "workspace-1",
        WorkspaceNodeId = "node-a",
        WorkspaceOwnerId = $"{Tenant}:operator/prj-1",
        SourceSnapshotHash = new string('a', 64),
        PlanInputHash = new string('b', 64),
        TargetProfileId = "sandbox",
        TargetProfileVersion = 1,
        TargetProfileHash = new string('c', 64),
        Request = new MigrationRunRequest
        {
            EngagementId = "ENG-1",
            ApplicationName = "ORDERS",
            RequestedMode = ExecutionMode.PlanOnly,
            Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
            SourceRoot = "forms",
            OutputRoot = ".fleet-run/runs/run-1/out",
        },
        EnqueuedUtc = enqueuedUtc,
    };
}
