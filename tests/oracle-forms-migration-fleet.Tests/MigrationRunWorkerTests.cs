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
        WorkbenchActor actor = WorkbenchActor.ForTenant(tenant, "operator", [WorkbenchRoles.MigrationOperator]);
        (PlatformAccessService platform, string projectId, PlatformTargetProfile profile) =
            await ProfileAsync("complete", actor);
        string owner = PlatformIdentity.WorkspaceOwner(actor, projectId);
        string workspaceId = await UploadAsync(workspaces, owner);
        string snapshotHash = workspaces.Describe(owner, workspaceId, "forms")!.SnapshotHash;
        FileMigrationRunStore store = new(Path.Combine(_root, "runs.json"));
        string runId = "run-worker";
        await store.EnqueueAsync(
            Run(runId, actor, projectId, owner, workspaceId, snapshotHash, profile) with
            {
                Request = new MigrationRunRequest
                {
                    EngagementId = "ENG-WORKER",
                    ApplicationName = "ORDERS",
                    RequestedMode = ExecutionMode.PlanOnly,
                    Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
                    SourceRoot = "forms",
                    OutputRoot = $".fleet-run/runs/{runId}/out",
                },
            },
            CancellationToken.None);

        ServiceProvider services = new ServiceCollection().AddSingleton(platform).BuildServiceProvider();
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
            WorkbenchAuthenticationOptions.DevelopmentTenantId, "operator", [WorkbenchRoles.MigrationOperator]);
        (PlatformAccessService platform, string projectId, PlatformTargetProfile profile) =
            await ProfileAsync("cancel", actor);
        string owner = PlatformIdentity.WorkspaceOwner(actor, projectId);
        string workspaceId = await UploadAsync(workspaces, owner);
        FileMigrationRunStore store = new(Path.Combine(_root, "cancel-runs.json"));
        MigrationRunRecord run = Run(
            "run-cancel",
            actor,
            projectId,
            owner,
            workspaceId,
            workspaces.Describe(owner, workspaceId, "forms")!.SnapshotHash,
            profile);
        await store.EnqueueAsync(run, CancellationToken.None);
        await store.RequestCancellationAsync(
            actor.TenantId, run.RunId, actor.ObjectId, DateTimeOffset.UtcNow, CancellationToken.None);
        await using ServiceProvider services = new ServiceCollection().AddSingleton(platform).BuildServiceProvider();
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
            WorkbenchAuthenticationOptions.DevelopmentTenantId, "operator", [WorkbenchRoles.MigrationOperator]);
        (PlatformAccessService platform, string projectId, PlatformTargetProfile profile) =
            await ProfileAsync("failure", actor);
        string owner = PlatformIdentity.WorkspaceOwner(actor, projectId);
        string workspaceId = await UploadAsync(workspaces, owner);
        FileMigrationRunStore store = new(Path.Combine(_root, "failure-runs.json"));
        MigrationRunRecord run = Run(
            "run-failure",
            actor,
            projectId,
            owner,
            workspaceId,
            workspaces.Describe(owner, workspaceId, "forms")!.SnapshotHash,
            profile) with
        {
            Request = Run("run-failure", actor, projectId, owner, workspaceId, new string('a', 64), profile).Request with
            {
                OutputRoot = "outside-durable-root",
            },
        };
        await store.EnqueueAsync(run, CancellationToken.None);
        await using ServiceProvider services = new ServiceCollection().AddSingleton(platform).BuildServiceProvider();
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

    /// <summary>
    /// A run that names a ledger is asking to generate under recorded decisions. A host that records none
    /// cannot read them, and generating anyway would emit a tier nothing decided. So the run stops before
    /// a phase starts, and says which capability is missing.
    /// </summary>
    [Fact]
    public async Task A_run_that_names_a_ledger_this_host_cannot_read_generates_nothing()
    {
        string workspaceRoot = Path.Combine(_root, "ledger-workspaces");
        using SourceWorkspaceService workspaces = new(workspaceRoot);
        WorkbenchActor actor = WorkbenchActor.ForTenant(
            WorkbenchAuthenticationOptions.DevelopmentTenantId, "operator", [WorkbenchRoles.MigrationOperator]);
        (PlatformAccessService platform, string projectId, PlatformTargetProfile profile) =
            await ProfileAsync("ledger", actor);
        string owner = PlatformIdentity.WorkspaceOwner(actor, projectId);
        string workspaceId = await UploadAsync(workspaces, owner);
        FileMigrationRunStore store = new(Path.Combine(_root, "ledger-runs.json"));
        MigrationRunRecord run = Run(
            "run-ledger",
            actor,
            projectId,
            owner,
            workspaceId,
            workspaces.Describe(owner, workspaceId, "forms")!.SnapshotHash,
            profile);
        run = run with { Request = run.Request with { DispositionLedgerId = "dled-absent" } };
        await store.EnqueueAsync(run, CancellationToken.None);

        await using ServiceProvider services = new ServiceCollection().AddSingleton(platform).BuildServiceProvider();
        MigrationRunWorker worker = new(
            store, workspaces, new WorkbenchAuthorizationService(), services, NullLogger<MigrationRunWorker>.Instance);

        MigrationRunRecord terminal = await RunToTerminalAsync(worker, store, run);

        Assert.Equal(MigrationRunState.Failed, terminal.State);
        Assert.Contains("records none", terminal.FailureReason!, StringComparison.Ordinal);
        Assert.Empty(await store.ArtifactsAsync(actor.TenantId, run.RunId, CancellationToken.None));
    }

    /// <summary>
    /// Without a platform store the profile this run was accepted against cannot be read at all, so the
    /// destination it would write to cannot be confirmed to be the approved one. An unreadable profile is
    /// not a matching profile, so the run stops rather than proceeding on the planner's gates alone.
    /// </summary>
    [Fact]
    public async Task A_host_that_records_no_platform_state_executes_no_run()
    {
        string workspaceRoot = Path.Combine(_root, "stateless-workspaces");
        using SourceWorkspaceService workspaces = new(workspaceRoot);
        WorkbenchActor actor = WorkbenchActor.ForTenant(
            WorkbenchAuthenticationOptions.DevelopmentTenantId, "operator", [WorkbenchRoles.MigrationOperator]);
        string projectId = "prj-stateless";
        string owner = PlatformIdentity.WorkspaceOwner(actor, projectId);
        string workspaceId = await UploadAsync(workspaces, owner);
        FileMigrationRunStore store = new(Path.Combine(_root, "stateless-runs.json"));
        MigrationRunRecord run = Run(
            "run-stateless",
            actor,
            projectId,
            owner,
            workspaceId,
            workspaces.Describe(owner, workspaceId, "forms")!.SnapshotHash,
            profile: null);
        await store.EnqueueAsync(run, CancellationToken.None);

        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        MigrationRunWorker worker = new(
            store, workspaces, new WorkbenchAuthorizationService(), services, NullLogger<MigrationRunWorker>.Instance);

        MigrationRunRecord terminal = await RunToTerminalAsync(worker, store, run);

        Assert.Equal(MigrationRunState.Failed, terminal.State);
        Assert.Contains("records no platform state", terminal.FailureReason!, StringComparison.Ordinal);
        Assert.Empty(await store.ArtifactsAsync(actor.TenantId, run.RunId, CancellationToken.None));
    }

    /// <summary>
    /// The stack was matched when the run was accepted, but a queued run is executed later. The profile
    /// that stands at execution is the one the destination is configured for, so a run that waited across
    /// a profile change is refused rather than written to a destination nobody approved it for.
    /// </summary>
    [Fact]
    public async Task A_queued_run_is_rechecked_against_the_target_profile_that_stands_when_it_is_claimed()
    {
        string workspaceRoot = Path.Combine(_root, "profile-workspaces");
        using SourceWorkspaceService workspaces = new(workspaceRoot);
        string tenant = WorkbenchAuthenticationOptions.DevelopmentTenantId;
        WorkbenchActor actor = WorkbenchActor.ForTenant(tenant, "operator", [WorkbenchRoles.MigrationOperator]);

        FilePlatformStateStore platformStore = new(Path.Combine(_root, "profile-state.json"));
        await platformStore.InitializeAsync(CancellationToken.None);
        PlatformAccessService platform = new(
            platformStore,
            new ConfiguredSandboxTargetBinding("pg.postgres.database.azure.com", "ofm_sandbox", "id-ofm", CanWrite: true));

        string projectId = (await platform.CreateProjectAsync(actor, "ORDERS", CancellationToken.None)).Value!.ProjectId;
        PlatformResult<PlatformTargetProfile> profile = await platform.EnsureConfiguredTargetProfileAsync(
            actor,
            projectId,
            new PlatformTargetProfileEnvironment
            {
                AzureTenantId = tenant,
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

        string owner = PlatformIdentity.WorkspaceOwner(actor, projectId);
        string workspaceId = await UploadAsync(workspaces, owner);
        FileMigrationRunStore store = new(Path.Combine(_root, "profile-runs.json"));

        // Enqueued naming the Java stack, which is no longer the stack the profile records.
        MigrationRunRecord run = Run(
            "run-profile",
            actor,
            projectId,
            owner,
            workspaceId,
            workspaces.Describe(owner, workspaceId, "forms")!.SnapshotHash);
        await store.EnqueueAsync(run, CancellationToken.None);

        await using ServiceProvider services = new ServiceCollection()
            .AddSingleton(platform)
            .BuildServiceProvider();
        MigrationRunWorker worker = new(
            store, workspaces, new WorkbenchAuthorizationService(), services, NullLogger<MigrationRunWorker>.Instance);

        MigrationRunRecord terminal = await RunToTerminalAsync(worker, store, run);

        Assert.Equal(MigrationRunState.Failed, terminal.State);
        Assert.Contains("back end is fixed at AspNetCore", terminal.FailureReason!, StringComparison.Ordinal);
        Assert.Contains("queued against an earlier profile", terminal.FailureReason!, StringComparison.Ordinal);
    }

    private static async Task<MigrationRunRecord> RunToTerminalAsync(
        MigrationRunWorker worker,
        FileMigrationRunStore store,
        MigrationRunRecord run)
    {
        await worker.StartAsync(CancellationToken.None);
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            MigrationRunRecord terminal;
            do
            {
                await Task.Delay(50, timeout.Token);
                terminal = (await store.GetAsync(run.TenantId, run.RunId, timeout.Token))!;
            }
            while (!terminal.IsTerminal);
            return terminal;
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A platform store holding one project and the immutable target profile the default requested stack
    /// matches, so a worker test exercises the profile recheck rather than the absence of one.
    /// </summary>
    private async Task<(PlatformAccessService Platform, string ProjectId, PlatformTargetProfile Profile)> ProfileAsync(
        string name,
        WorkbenchActor actor)
    {
        FilePlatformStateStore store = new(Path.Combine(_root, $"{name}-state.json"));
        await store.InitializeAsync(CancellationToken.None);
        PlatformAccessService platform = new(
            store,
            new ConfiguredSandboxTargetBinding("pg.postgres.database.azure.com", "ofm_sandbox", "id-ofm", CanWrite: true));

        PlatformResult<PlatformProject> project = await platform.CreateProjectAsync(actor, "ORDERS", CancellationToken.None);
        Assert.True(project.Succeeded, project.Error);

        PlatformResult<PlatformTargetProfile> profile = await platform.EnsureConfiguredTargetProfileAsync(
            actor,
            project.Value!.ProjectId,
            new PlatformTargetProfileEnvironment
            {
                AzureTenantId = actor.TenantId,
                SubscriptionId = "4d1a0e6f-9b77-4b5e-a0ef-2c7d6a41f8b2",
                ResourceGroup = "rg-dev",
                ResourceId = "/subscriptions/4d1a0e6f/resourceGroups/rg-dev/providers/Microsoft.DBforPostgreSQL/flexibleServers/pg",
                Region = "eastus2",
                SchemaName = "public",
                EnvironmentName = "sandbox",
            },
            CancellationToken.None);
        Assert.True(profile.Succeeded, profile.Error);

        return (platform, project.Value.ProjectId, profile.Value!);
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
        string snapshotHash,
        PlatformTargetProfile? profile = null) => new()
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
        TargetProfileId = profile?.TargetProfileId ?? "sandbox",
        TargetProfileVersion = profile?.Version ?? 1,
        TargetProfileHash = profile?.CanonicalHash ?? new string('c', 64),
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
