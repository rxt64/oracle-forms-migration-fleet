// Copyright (c) Microsoft. All rights reserved.

using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

public sealed class MigrationRunWorkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ofm-worker-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Worker_completes_a_queued_run_without_an_attached_event_follower()
    {
        string workspaceRoot = Path.Combine(_root, "workspaces");
        using SourceWorkspaceService workspaces = new(workspaceRoot);
        string tenant = WorkbenchAuthenticationOptions.DevelopmentTenantId;
        WorkbenchActor actor = WorkbenchActor.ForTenant(tenant, "operator", []);
        string projectId = "prj-worker";
        string owner = PlatformIdentity.WorkspaceOwner(actor, projectId);
        string workspaceId = await UploadAsync(workspaces, owner);
        string snapshotHash = workspaces.Describe(owner, workspaceId, "forms")!.SnapshotHash;
        FileMigrationRunStore store = new(Path.Combine(_root, "runs.json"));
        string runId = "run-worker";
        await store.EnqueueAsync(
            new MigrationRunRecord
            {
                RunId = runId,
                TenantId = tenant,
                ProjectId = projectId,
                ActorObjectId = actor.ObjectId,
                WorkspaceId = workspaceId,
                WorkspaceNodeId = MigrationRunNode.Current,
                WorkspaceOwnerId = owner,
                SourceSnapshotHash = snapshotHash,
                PlanInputHash = new string('b', 64),
                TargetProfileId = "sandbox",
                TargetProfileVersion = 1,
                TargetProfileHash = new string('c', 64),
                Request = new MigrationRunRequest
                {
                    EngagementId = "ENG-WORKER",
                    ApplicationName = "ORDERS",
                    RequestedMode = ExecutionMode.PlanOnly,
                    Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
                    SourceRoot = "forms",
                    OutputRoot = $".fleet-run/runs/{runId}/out",
                },
                EnqueuedUtc = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        MigrationRunWorker worker = new(
            store,
            workspaces,
            new WorkbenchAuthorizationService(),
            services,
            NullLogger<MigrationRunWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            MigrationRunRecord terminal;
            do
            {
                await Task.Delay(50, timeout.Token);
                terminal = (await store.GetAsync(tenant, runId, timeout.Token))!;
            }
            while (!terminal.IsTerminal);

            Assert.Equal(MigrationRunState.Failed, terminal.State);
            IReadOnlyList<MigrationRunEvent> events = await store.EventsAsync(tenant, runId, 0, CancellationToken.None);
            Assert.NotEmpty(events);
            Assert.Equal(Enumerable.Range(1, events.Count).Select(value => (long)value), events.Select(item => item.Sequence));
            Assert.Equal("Failed", events[^1].Signal!.State.ToString());
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            await services.DisposeAsync();
        }
    }

    [Fact]
    public async Task Pre_start_cancellation_does_not_retain_the_workspace()
    {
        string workspaceRoot = Path.Combine(_root, "cancel-workspaces");
        using SourceWorkspaceService workspaces = new(workspaceRoot);
        WorkbenchActor actor = WorkbenchActor.ForTenant(
            WorkbenchAuthenticationOptions.DevelopmentTenantId, "operator", []);
        string projectId = "prj-cancel";
        string owner = PlatformIdentity.WorkspaceOwner(actor, projectId);
        string workspaceId = await UploadAsync(workspaces, owner);
        FileMigrationRunStore store = new(Path.Combine(_root, "cancel-runs.json"));
        MigrationRunRecord run = Run(
            "run-cancel",
            actor,
            projectId,
            owner,
            workspaceId,
            workspaces.Describe(owner, workspaceId, "forms")!.SnapshotHash);
        await store.EnqueueAsync(run, CancellationToken.None);
        await store.RequestCancellationAsync(
            actor.TenantId, run.RunId, actor.ObjectId, DateTimeOffset.UtcNow, CancellationToken.None);
        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        MigrationRunWorker worker = new(
            store, workspaces, new WorkbenchAuthorizationService(), services, NullLogger<MigrationRunWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            while (!(await store.GetAsync(actor.TenantId, run.RunId, timeout.Token))!.IsTerminal)
            {
                await Task.Delay(50, timeout.Token);
            }
            Assert.False(workspaces.IsRetained(workspaceId));
            Assert.True(workspaces.Release(owner, workspaceId));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Post_start_setup_failure_releases_retention_and_records_failure()
    {
        string workspaceRoot = Path.Combine(_root, "failure-workspaces");
        using SourceWorkspaceService workspaces = new(workspaceRoot);
        WorkbenchActor actor = WorkbenchActor.ForTenant(
            WorkbenchAuthenticationOptions.DevelopmentTenantId, "operator", []);
        string projectId = "prj-failure";
        string owner = PlatformIdentity.WorkspaceOwner(actor, projectId);
        string workspaceId = await UploadAsync(workspaces, owner);
        FileMigrationRunStore store = new(Path.Combine(_root, "failure-runs.json"));
        MigrationRunRecord run = Run(
            "run-failure",
            actor,
            projectId,
            owner,
            workspaceId,
            workspaces.Describe(owner, workspaceId, "forms")!.SnapshotHash) with
        {
            Request = Run("run-failure", actor, projectId, owner, workspaceId, new string('a', 64)).Request with
            {
                OutputRoot = "outside-durable-root",
            },
        };
        await store.EnqueueAsync(run, CancellationToken.None);
        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        MigrationRunWorker worker = new(
            store, workspaces, new WorkbenchAuthorizationService(), services, NullLogger<MigrationRunWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            MigrationRunRecord persisted;
            do
            {
                await Task.Delay(50, timeout.Token);
                persisted = (await store.GetAsync(actor.TenantId, run.RunId, timeout.Token))!;
            }
            while (!persisted.IsTerminal);
            Assert.Equal(MigrationRunState.Failed, persisted.State);
            Assert.False(workspaces.IsRetained(workspaceId));
            Assert.True(workspaces.Release(owner, workspaceId));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<string> UploadAsync(SourceWorkspaceService workspaces, string owner)
    {
        using MemoryStream archive = new();
        using (ZipArchive zip = new(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using Stream entry = zip.CreateEntry("forms/ORDERS.fmb").Open();
            await entry.WriteAsync("form"u8.ToArray());
        }
        archive.Position = 0;
        string? workspaceId = null;
        await foreach (SourceProgress progress in workspaces.ExtractAsync(
            owner, archive, "orders.zip", CancellationToken.None))
        {
            if (progress.Level == "done") workspaceId = progress.Text;
        }
        return workspaceId ?? throw new InvalidOperationException("The test workspace was not created.");
    }

    private static MigrationRunRecord Run(
        string runId,
        WorkbenchActor actor,
        string projectId,
        string owner,
        string workspaceId,
        string snapshotHash) => new()
    {
        RunId = runId,
        TenantId = actor.TenantId,
        ProjectId = projectId,
        ActorObjectId = actor.ObjectId,
        WorkspaceId = workspaceId,
        WorkspaceNodeId = MigrationRunNode.Current,
        WorkspaceOwnerId = owner,
        SourceSnapshotHash = snapshotHash,
        PlanInputHash = new string('b', 64),
        TargetProfileId = "sandbox",
        TargetProfileVersion = 1,
        TargetProfileHash = new string('c', 64),
        Request = new MigrationRunRequest
        {
            EngagementId = "ENG-CANCEL",
            ApplicationName = "ORDERS",
            RequestedMode = ExecutionMode.PlanOnly,
            Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
            SourceRoot = "forms",
            OutputRoot = $".fleet-run/runs/{runId}/out",
        },
        EnqueuedUtc = DateTimeOffset.UtcNow,
    };
}
