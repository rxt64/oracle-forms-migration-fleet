// Copyright (c) Microsoft. All rights reserved.

using Npgsql;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

public sealed class PostgresPlatformStateIntegrationTests : IAsyncLifetime
{
    private const string ConnectionVariable = "PLATFORM_POSTGRES_INTEGRATION_CONNECTION";
    private const string RequiredVariable = "REQUIRE_POSTGRES_INTEGRATION";
    private const string Tenant = "8f1e2b42-6a1d-4a24-9ad2-6c1b6a8f3d71";
    private const string Requester = "3b4c9a10-7d42-4f0e-9d51-2a61f0c4b8e3";
    private const string Approver = "9c2d7e51-0b83-4a6f-8c19-5d7e2f1a4b60";

    /// <summary>A lease no sequence of round trips in one test can outlive.</summary>
    private static readonly TimeSpan LiveLease = TimeSpan.FromSeconds(30);

    /// <summary>A lease that ended before the statement granting it, expiring on the database clock.</summary>
    private static readonly TimeSpan ExpiredLease = TimeSpan.FromSeconds(-1);

    private readonly string _schema = $"ofm_test_{Guid.NewGuid():N}";
    private string? _connectionString;

    public Task InitializeAsync()
    {
        _connectionString = System.Environment.GetEnvironmentVariable(ConnectionVariable);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_connectionString is null)
        {
            return;
        }

        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new($"drop schema if exists {_schema} cascade", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Full_authorization_lifecycle_round_trips_through_real_postgresql()
    {
        PostgresPlatformStateStore? configured = CreateStore();
        if (configured is null)
        {
            RequireConfiguredConnection();
            return;
        }
        await using PostgresPlatformStateStore store = configured;

        await store.InitializeAsync(CancellationToken.None);
        ConfiguredSandboxTargetBinding sandbox = new(
            "pg-sandbox.postgres.database.azure.com", "ofm_sandbox", "id-ofmfleet-web-dev", CanWrite: true);
        PostgresSandboxProjectBindingStore sandboxProjects = new(Options(), OpenAsync);
        PlatformAccessService platform = new(
            store,
            sandbox,
            sandboxProjects: sandboxProjects);
        WorkbenchActor requester = Actor(Requester);
        WorkbenchActor approver = Actor(Approver);

        PlatformProject project = (await platform.CreateProjectAsync(
            requester, "PostgreSQL integration", CancellationToken.None)).Value!;
        await platform.AddMemberAsync(
            requester,
            project.ProjectId,
            approver.ObjectId,
            [WorkbenchRoles.MigrationOperator, WorkbenchRoles.SandboxApprover],
            CancellationToken.None);

        PlatformTargetProfile profile = (await platform.EnsureConfiguredTargetProfileAsync(
            requester,
            project.ProjectId,
            TargetEnvironment(),
            CancellationToken.None)).Value!;
        PlatformTargetProfile? latest = await store.GetTargetProfileAsync(
            Tenant, project.ProjectId, profile.TargetProfileId, version: null, CancellationToken.None);
        Assert.Equal(profile.CanonicalHash, latest!.CanonicalHash);
        Assert.Equal(2, (await store.MembershipsAsync(Tenant, project.ProjectId, CancellationToken.None)).Count);
        Assert.Single(await store.TargetProfilesAsync(Tenant, project.ProjectId, CancellationToken.None));

        PlatformApproval requested = (await platform.RequestAsync(
            requester,
            new PlatformApprovalRequestInput(
                project.ProjectId,
                profile.TargetProfileId,
                WorkbenchMutationScope.SandboxDatabaseWrite,
                "ENG-POSTGRES",
                "source-hash",
                "plan-hash",
                TimeSpan.FromHours(1),
                "Integration request",
                ProfileTarget),
            CancellationToken.None)).Value!;
        PlatformApproval approved = (await platform.DecideAsync(
            approver, requested.ApprovalId, approve: true, requested.Version, "Approved", CancellationToken.None)).Value!;
        Assert.Single(await store.ApprovalsForProjectAsync(Tenant, project.ProjectId, CancellationToken.None));
        Assert.Single(await store.ApprovalsForRequesterAsync(Tenant, Requester, CancellationToken.None));

        Assert.Single(await new PlatformAuthorizationStore(store, sandbox, sandboxProjects: sandboxProjects)
            .ForOwnerAsync(requester.OwnerId, CancellationToken.None));

        PlatformApproval revoked = (await platform.RevokeAsync(
            requester, approved.ApprovalId, approved.Version, "Complete", CancellationToken.None)).Value!;
        Assert.Equal(PlatformApprovalState.Revoked, revoked.State);
        Assert.Empty(await new PlatformAuthorizationStore(store, sandbox, sandboxProjects: sandboxProjects)
            .ForOwnerAsync(requester.OwnerId, CancellationToken.None));
        Assert.Equal(
            ["Requested", "Approved", "Revoked"],
            await ApprovalEventsAsync(approved.ApprovalId));

        PlatformProject otherProject = (await platform.CreateProjectAsync(
            requester, "Other PostgreSQL project", CancellationToken.None)).Value!;
        Assert.True((await platform.EnsureConfiguredTargetProfileAsync(
            requester, otherProject.ProjectId, TargetEnvironment(), CancellationToken.None)).Succeeded);
        PlatformResult<PlatformApproval> denied = await platform.RequestAsync(
            requester,
            new PlatformApprovalRequestInput(
                otherProject.ProjectId,
                "sandbox",
                WorkbenchMutationScope.SandboxDatabaseWrite,
                "ENG-OTHER",
                "other-source",
                "other-plan",
                TimeSpan.FromHours(1),
                null,
                ProfileTarget),
            CancellationToken.None);
        Assert.Equal(409, denied.Status);
        Assert.True((await platform.RequestAsync(
            requester,
            new PlatformApprovalRequestInput(
                otherProject.ProjectId,
                "sandbox",
                WorkbenchMutationScope.ValidationOnly,
                "ENG-VALIDATION",
                "validation-source",
                "validation-plan",
                TimeSpan.FromMinutes(5),
                null,
                ProfileTarget),
            CancellationToken.None)).Succeeded);

        await using PostgresPlatformStateStore restarted = CreateStore()!;
        await restarted.InitializeAsync(CancellationToken.None);
        PlatformApproval? persisted = await restarted.GetApprovalAsync(Tenant, approved.ApprovalId, CancellationToken.None);
        Assert.Equal(PlatformApprovalState.Revoked, persisted!.State);
        Assert.Contains(
            await restarted.ProjectsForActorAsync(Tenant, Requester, CancellationToken.None),
            candidate => candidate.ProjectId == project.ProjectId);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Source_environment_profiles_round_trip_through_schema_v4()
    {
        PostgresPlatformStateStore? configured = CreateStore();
        if (configured is null)
        {
            RequireConfiguredConnection();
            return;
        }
        await using PostgresPlatformStateStore store = configured;
        await store.InitializeAsync(CancellationToken.None);
        PlatformAccessService platform = new(store, sandbox: null);
        WorkbenchActor requester = Actor(Requester);
        PlatformProject project = (await platform.CreateProjectAsync(
            requester, "Source profile integration", CancellationToken.None)).Value!;
        SourceEnvironmentDeclaration declaration = new(
            "legacy-order-entry",
            "Legacy Order Entry",
            SourceConnector.FormsBuilderWorker,
            "6i",
            "9i",
            "legacy-order-entry",
            ["LEGACY_LAB"],
            ["oracle-source-credential"]);

        SourceEnvironmentProfile first = (await platform.EnsureSourceEnvironmentProfileAsync(
            requester, project.ProjectId, declaration, CancellationToken.None)).Value!;
        SourceEnvironmentProfile same = (await platform.EnsureSourceEnvironmentProfileAsync(
            requester, project.ProjectId, declaration, CancellationToken.None)).Value!;
        SourceEnvironmentProfile changed = (await platform.EnsureSourceEnvironmentProfileAsync(
            requester,
            project.ProjectId,
            declaration with { PathAlias = "legacy-order-entry-v2" },
            CancellationToken.None)).Value!;
        SourceEnvironmentProfile? exact = await store.GetSourceEnvironmentProfileAsync(
            Tenant, project.ProjectId, declaration.SourceEnvironmentId, 1, CancellationToken.None);

        Assert.Equal(first.CanonicalHash, same.CanonicalHash);
        Assert.Equal(2, changed.Version);
        Assert.Equal(first.CanonicalHash, exact!.CanonicalHash);
        Assert.Single(await store.SourceEnvironmentProfilesAsync(Tenant, project.ProjectId, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Concurrent_initialization_applies_each_migration_once()
    {
        PostgresPlatformStateStore? configured = CreateStore();
        if (configured is null)
        {
            RequireConfiguredConnection();
            return;
        }
        await using PostgresPlatformStateStore first = configured;

        await using PostgresPlatformStateStore second = CreateStore()!;
        await Task.WhenAll(
            first.InitializeAsync(CancellationToken.None),
            second.InitializeAsync(CancellationToken.None));

        await using NpgsqlConnection connection = await OpenAsync(CancellationToken.None);
        await using NpgsqlCommand command = new(
            $"select version from {_schema}.schema_version order by version", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        List<int> versions = [];
        while (await reader.ReadAsync())
        {
            versions.Add(reader.GetInt32(0));
        }

        Assert.Equal(Enumerable.Range(1, PlatformSchema.CurrentVersion), versions);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Concurrent_projects_cannot_both_bind_the_shared_postgresql_sandbox()
    {
        PostgresPlatformStateStore? configured = CreateStore();
        if (configured is null)
        {
            RequireConfiguredConnection();
            return;
        }
        await using PostgresPlatformStateStore store = configured;
        await store.InitializeAsync(CancellationToken.None);
        ConfiguredSandboxTargetBinding sandbox = new(
            "pg-sandbox.postgres.database.azure.com", "ofm_sandbox", "id-ofmfleet-web-dev", CanWrite: true);
        PlatformAccessService platform = new(
            store,
            sandbox,
            sandboxProjects: new PostgresSandboxProjectBindingStore(Options(), OpenAsync));
        WorkbenchActor requester = Actor(Requester);
        PlatformProject first = await CreateProjectWithProfileAsync(platform, requester, "Concurrent first");
        PlatformProject second = await CreateProjectWithProfileAsync(platform, requester, "Concurrent second");

        PlatformResult<PlatformApproval>[] results = await Task.WhenAll(
            platform.RequestAsync(requester, ApprovalInput(first.ProjectId, "ENG-FIRST"), CancellationToken.None),
            platform.RequestAsync(requester, ApprovalInput(second.ProjectId, "ENG-SECOND"), CancellationToken.None));

        Assert.Single(results, result => result.Succeeded);
        Assert.Single(results, result => !result.Succeeded && result.Status == 409);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Version_two_migration_binds_the_earliest_legacy_sandbox_request()
    {
        PostgresPlatformStateStore? configured = CreateStore();
        if (configured is null)
        {
            RequireConfiguredConnection();
            return;
        }
        await using PostgresPlatformStateStore store = configured;
        await InitializeVersionOneAsync();
        PlatformAccessService platform = new(store, sandbox: null);
        WorkbenchActor requester = Actor(Requester);
        PlatformProject first = (await platform.CreateProjectAsync(
            requester, "Legacy first", CancellationToken.None)).Value!;
        PlatformProject second = (await platform.CreateProjectAsync(
            requester, "Legacy second", CancellationToken.None)).Value!;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await store.CreateApprovalAsync(LegacyApproval(first.ProjectId, "ENG-EARLY", now), CancellationToken.None);
        await store.CreateApprovalAsync(
            LegacyApproval(second.ProjectId, "ENG-LATE", now.AddMinutes(1)) with
            {
                State = PlatformApprovalState.Approved,
                DecidedByObjectId = Approver,
                DecidedUtc = now.AddMinutes(2),
            },
            CancellationToken.None);

        await store.InitializeAsync(CancellationToken.None);
        PostgresSandboxProjectBindingStore binding = new(Options(), OpenAsync);
        Assert.Equal(first.ProjectId, await binding.GetSandboxProjectAsync(Tenant, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Durable_run_claims_events_fences_history_and_artifacts_round_trip()
    {
        PostgresPlatformStateStore? configured = CreateStore();
        if (configured is null)
        {
            RequireConfiguredConnection();
            return;
        }
        await using PostgresPlatformStateStore platformStore = configured;
        await platformStore.InitializeAsync(CancellationToken.None);
        PlatformAccessService platform = new(platformStore, sandbox: null);
        PlatformProject project = (await platform.CreateProjectAsync(
            Actor(Requester), "Durable run", CancellationToken.None)).Value!;
        PostgresMigrationRunStore runs = new(Options(), OpenAsync);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MigrationRunRecord run = await runs.EnqueueAsync(DurableRun(project.ProjectId, now), CancellationToken.None);

        MigrationRunClaim first = (await runs.ClaimAsync(
            "node-a", "worker-a", now, LiveLease, CancellationToken.None))!;
        Assert.Null(await runs.ClaimAsync(
            "node-a", "worker-b", now, LiveLease, CancellationToken.None));
        Assert.NotNull(await runs.AppendEventAsync(
            run.RunId, first.FenceToken, now, "info", "first", null, null, CancellationToken.None));

        // The lease clock is the database's own now(), so expiry is forced by renewing onto a lease that
        // already ended rather than by outliving a short one, which the round trips above can do on their own.
        Assert.True(await runs.RenewAsync(run.RunId, first.FenceToken, ExpiredLease, CancellationToken.None));
        Assert.False(await runs.RenewAsync(
            run.RunId, first.FenceToken, LiveLease, CancellationToken.None));
        Assert.Null(await runs.AppendEventAsync(
            run.RunId, first.FenceToken, now, "info", "expired", null, null, CancellationToken.None));
        MigrationRunClaim second = (await runs.ClaimAsync(
            "node-a", "worker-b", now.AddMinutes(1), LiveLease, CancellationToken.None))!;
        Assert.Equal(first.FenceToken + 1, second.FenceToken);
        Assert.Null(await runs.AppendEventAsync(
            run.RunId, first.FenceToken, now, "info", "stale", null, null, CancellationToken.None));
        Assert.NotNull(await runs.AppendEventAsync(
            run.RunId, second.FenceToken, now, "info", "second", null, null, CancellationToken.None));
        Assert.True(await runs.CompleteAsync(
            run.RunId,
            second.FenceToken,
            MigrationRunState.Succeeded,
            now.AddMinutes(2),
            null,
            null,
            [new MigrationRunArtifact(run.RunId, ".fleet-run/runs/run-postgres/report.md", "Report", "Result", 6, new string('d', 64))],
            "done",
            new ProgressSignal(
                ProgressOperations.MigrationRun,
                ProgressActions.RunCompleted,
                ProgressState.Completed,
                "Integration run",
                "Completed",
                "Review"),
            CancellationToken.None));

        Assert.Equal([1L, 2L, 3L], (await runs.EventsAsync(Tenant, run.RunId, 0, CancellationToken.None)).Select(item => item.Sequence));
        Assert.Single(await runs.ForProjectAsync(Tenant, project.ProjectId, 10, CancellationToken.None));
        Assert.Single(await runs.ArtifactsAsync(Tenant, run.RunId, CancellationToken.None));
        Assert.Equal(MigrationRunState.Succeeded, (await runs.GetAsync(Tenant, run.RunId, CancellationToken.None))!.State);

        MigrationRunRecord cancelledRun = await runs.EnqueueAsync(
            DurableRun(project.ProjectId, now, "run-cancel"), CancellationToken.None);
        MigrationRunClaim cancelClaim = (await runs.ClaimAsync(
            "node-a", "worker-c", now, LiveLease, CancellationToken.None))!;
        Assert.True(await runs.MarkRunningAsync(
            cancelledRun.RunId, cancelClaim.FenceToken, now, CancellationToken.None));
        Assert.True(await runs.RequestCancellationAsync(
            Tenant, cancelledRun.RunId, Requester, now, CancellationToken.None));
        Assert.False(await runs.CompleteAsync(
            cancelledRun.RunId, cancelClaim.FenceToken, MigrationRunState.Succeeded, now, null, null, [],
            "done", RunSignal(ProgressState.Completed), CancellationToken.None));
        Assert.True(await runs.CompleteAsync(
            cancelledRun.RunId, cancelClaim.FenceToken, MigrationRunState.Cancelled, now, null, "Cancelled", [],
            "error", RunSignal(ProgressState.Failed), CancellationToken.None));
        Assert.Single(await runs.EventsAsync(Tenant, cancelledRun.RunId, 0, CancellationToken.None));

        MigrationRunRecord interruptedRun = await runs.EnqueueAsync(
            DurableRun(project.ProjectId, now, "run-interrupted"), CancellationToken.None);
        MigrationRunClaim interruptedClaim = (await runs.ClaimAsync(
            "node-a", "worker-d", now, LiveLease, CancellationToken.None))!;
        Assert.True(await runs.MarkRunningAsync(
            interruptedRun.RunId, interruptedClaim.FenceToken, now, CancellationToken.None));
        Assert.True(await runs.RenewAsync(
            interruptedRun.RunId, interruptedClaim.FenceToken, ExpiredLease, CancellationToken.None));
        Assert.Equal(1, await runs.ReconcileExpiredAsync(
            "node-b", DateTimeOffset.UtcNow, "Replica stopped.", CancellationToken.None));
        Assert.Equal(
            MigrationRunState.Interrupted,
            (await runs.GetAsync(Tenant, interruptedRun.RunId, CancellationToken.None))!.State);
        Assert.Single(await runs.EventsAsync(Tenant, interruptedRun.RunId, 0, CancellationToken.None));
    }

    /// <summary>
    /// The all-or-nothing ledger batch, against the real transaction rather than the file store's
    /// document gate.
    ///
    /// The losing row is the middle one on purpose: a loop would already have written the row before it
    /// by the time it discovered the conflict, and in PostgreSQL that half-written state is only undone
    /// by the rollback. The winning case then proves the same statement advances every row exactly once,
    /// and that entering evidence against a row leaves its decision revision where it was — which is what
    /// keeps an authorization issued over these decisions describing them afterwards.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task A_batch_of_ledger_updates_is_all_or_nothing_in_one_real_transaction()
    {
        PostgresPlatformStateStore? configured = CreateStore();
        if (configured is null)
        {
            RequireConfiguredConnection();
            return;
        }
        await using PostgresPlatformStateStore store = configured;
        await store.InitializeAsync(CancellationToken.None);

        IReadOnlyList<DispositionLedgerEntry> seeded = await SeedLedgerAsync(store, "dled-pg-batch");
        Assert.Equal(3, seeded.Count);

        // Somebody else moved the middle row after these three versions were read.
        DispositionLedgerEntry moved = (await store.UpdateDispositionLedgerEntryAsync(
            seeded[1] with { Rationale = "Decided elsewhere." }, seeded[1].Version, CancellationToken.None))!;

        DispositionGeneratedReference reference = new(
            "run-pg-batch", ".fleet-run/out/application", new string('d', 64), DateTimeOffset.UtcNow, new string('e', 64), 7);

        Assert.Null(await store.UpdateDispositionLedgerEntriesAsync(
            [.. seeded.Select(entry => new DispositionLedgerEntryUpdate(
                entry with { GeneratedRefs = [reference] }, entry.Version))],
            CancellationToken.None));

        IReadOnlyList<DispositionLedgerEntry> rolledBack =
            await store.DispositionLedgerEntriesAsync(Tenant, "dled-pg-batch", CancellationToken.None);

        Assert.All(rolledBack, entry => Assert.Empty(entry.GeneratedRefs));
        Assert.Equal(seeded[0].Version, rolledBack.Single(entry => entry.EntryId == seeded[0].EntryId).Version);
        Assert.Equal(seeded[2].Version, rolledBack.Single(entry => entry.EntryId == seeded[2].EntryId).Version);
        Assert.Equal(moved.Version, rolledBack.Single(entry => entry.EntryId == moved.EntryId).Version);

        IReadOnlyList<DispositionLedgerEntry>? written = await store.UpdateDispositionLedgerEntriesAsync(
            [.. rolledBack.Select(entry => new DispositionLedgerEntryUpdate(
                entry with { GeneratedRefs = [reference] }, entry.Version))],
            CancellationToken.None);

        Assert.NotNull(written);
        Assert.Equal(rolledBack.Count, written.Count);

        IReadOnlyList<DispositionLedgerEntry> committed =
            await store.DispositionLedgerEntriesAsync(Tenant, "dled-pg-batch", CancellationToken.None);

        Assert.All(committed, entry => Assert.Equal(reference, entry.GeneratedRefs.Single()));
        Assert.All(committed, entry => Assert.Equal(
            rolledBack.Single(before => before.EntryId == entry.EntryId).Version + 1, entry.Version));

        // The row version advanced on every row and the decision on none of them.
        Assert.All(committed, entry => Assert.Equal(
            rolledBack.Single(before => before.EntryId == entry.EntryId).DecisionRevision, entry.DecisionRevision));
    }

    private async Task<IReadOnlyList<DispositionLedgerEntry>> SeedLedgerAsync(
        IPlatformStateStore store,
        string ledgerId)
    {
        const string snapshot = "cafe0000000000000000000000000000000000000000000000000000000000ff";
        DateTimeOffset now = DateTimeOffset.UtcNow;

        PlatformAccessService platform = new(store, sandbox: null);
        PlatformProject project = (await platform.CreateProjectAsync(
            Actor(Requester), $"Ledger batch {ledgerId}", CancellationToken.None)).Value!;

        DispositionLedgerEntry[] entries =
        [
            .. new[] { "ORD_NO", "ORD_DATE", "ORD_TOTAL" }.Select(property => new DispositionLedgerEntry
            {
                LedgerId = ledgerId,
                EntryId = $"{ledgerId}-{property}",
                TenantId = Tenant,
                ProjectId = project.ProjectId,
                SourceSnapshotHash = snapshot,
                Identity = new DispositionSourceFactIdentity(
                    "ORDERS", "legacy/forms/ORDERS.xml", "{ns}Block[1]", "Item", property, "Data entry"),
                ObservedValue = property,
                ObservedEvidence = "Declared by the Forms export.",
            }),
        ];

        Assert.NotNull(await store.CreateDispositionLedgerAsync(
            new DispositionLedger
            {
                LedgerId = ledgerId,
                TenantId = Tenant,
                ProjectId = project.ProjectId,
                RunId = $"run-{ledgerId}",
                SourceSnapshotHash = snapshot,
                SourceRoot = "legacy/forms",
                IntermediateContentSha256 = new string('a', 64),
                CreatedUtc = now,
                CreatedByObjectId = Requester,
                ModuleCount = 1,
                EntryCount = entries.Length,
            },
            entries,
            CancellationToken.None));

        return await store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None);
    }

    private PostgresPlatformStateStore? CreateStore()
    {
        if (_connectionString is null)
        {
            return null;
        }

        return new PostgresPlatformStateStore(Options(), OpenAsync);
    }

    private PlatformDatabaseOptions Options()
    {
        NpgsqlConnectionStringBuilder builder = new(_connectionString!);
        if (string.IsNullOrWhiteSpace(builder.Host) ||
            string.IsNullOrWhiteSpace(builder.Database) ||
            string.IsNullOrWhiteSpace(builder.Username))
        {
            throw new InvalidOperationException("The PostgreSQL integration connection must name a host, database, and user.");
        }

        return new PlatformDatabaseOptions
        {
            Host = builder.Host!,
            Database = builder.Database,
            User = builder.Username,
            Schema = _schema,
        };
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = new(_connectionString!);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task<IReadOnlyList<string>> ApprovalEventsAsync(string approvalId)
    {
        await using NpgsqlConnection connection = await OpenAsync(CancellationToken.None);
        await using NpgsqlCommand command = new(
            $"select action from {_schema}.approval_event where approval_id = @approval order by event_id", connection);
        command.Parameters.AddWithValue("approval", approvalId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        List<string> actions = [];
        while (await reader.ReadAsync())
        {
            actions.Add(reader.GetString(0));
        }

        return actions;
    }

    private async Task InitializeVersionOneAsync()
    {
        await using NpgsqlConnection connection = await OpenAsync(CancellationToken.None);
        await using (NpgsqlCommand ledger = new(PlatformSchema.LedgerStatement(_schema), connection))
        {
            await ledger.ExecuteNonQueryAsync();
        }

        PlatformSchema.Migration migration = PlatformSchema.Migrations(_schema)[0];
        foreach (string statement in migration.Statements)
        {
            await using NpgsqlCommand command = new(statement, connection);
            await command.ExecuteNonQueryAsync();
        }

        await using NpgsqlCommand record = new(PlatformSchema.RecordVersionStatement(_schema), connection);
        record.Parameters.AddWithValue("version", migration.Version);
        record.Parameters.AddWithValue("name", migration.Name);
        await record.ExecuteNonQueryAsync();
    }

    private static async Task<PlatformProject> CreateProjectWithProfileAsync(
        PlatformAccessService platform,
        WorkbenchActor requester,
        string name)
    {
        PlatformProject project = (await platform.CreateProjectAsync(
            requester, name, CancellationToken.None)).Value!;
        Assert.True((await platform.EnsureConfiguredTargetProfileAsync(
            requester, project.ProjectId, TargetEnvironment(), CancellationToken.None)).Succeeded);
        return project;
    }

    private static PlatformApprovalRequestInput ApprovalInput(string projectId, string engagementId) =>
        new(
            projectId,
            "sandbox",
            WorkbenchMutationScope.SandboxDatabaseWrite,
            engagementId,
            $"source-{engagementId}",
            $"plan-{engagementId}",
            TimeSpan.FromHours(1),
            null,
            ProfileTarget);

    /// <summary>The stack the configured profile records, which is what a compatible request names.</summary>
    private static TargetStack ProfileTarget => new()
    {
        Database = DatabaseTarget.PostgreSql,
        FrontEnd = FrontEndStack.React,
        BackEnd = BackEndStack.JavaSpringBoot,
    };

    private static PlatformApproval LegacyApproval(
        string projectId, string engagementId, DateTimeOffset requestedUtc) => new()
    {
        ApprovalId = $"apr-{Guid.NewGuid():N}",
        ProjectId = projectId,
        TenantId = Tenant,
        RequestedByObjectId = Requester,
        RequestedUtc = requestedUtc,
        State = PlatformApprovalState.Requested,
        Scope = WorkbenchMutationScope.SandboxDatabaseWrite,
        RequiredRole = WorkbenchRoles.MigrationOperator,
        EngagementId = engagementId,
        SourceSnapshotHash = $"source-{engagementId}",
        PlanInputHash = $"plan-{engagementId}",
        TargetProfileId = "sandbox",
        TargetProfileVersion = 1,
        TargetProfileHash = new string('a', 64),
        ExpiresUtc = requestedUtc.AddHours(1),
    };

    private static MigrationRunRecord DurableRun(
        string projectId, DateTimeOffset enqueuedUtc, string runId = "run-postgres") => new()
    {
        RunId = runId,
        TenantId = Tenant,
        ProjectId = projectId,
        ActorObjectId = Requester,
        WorkspaceId = "workspace-postgres",
        WorkspaceNodeId = "node-a",
        WorkspaceOwnerId = $"{Tenant}:{Requester}/{projectId}",
        SourceSnapshotHash = new string('a', 64),
        PlanInputHash = new string('b', 64),
        TargetProfileId = "sandbox",
        TargetProfileVersion = 1,
        TargetProfileHash = new string('c', 64),
        Request = new MigrationRunRequest
        {
            EngagementId = "ENG-DURABLE",
            ApplicationName = "ORDERS",
            RequestedMode = ExecutionMode.PlanOnly,
            Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
            SourceRoot = "forms",
            OutputRoot = $".fleet-run/runs/{runId}/out",
        },
        EnqueuedUtc = enqueuedUtc,
    };

    private static ProgressSignal RunSignal(ProgressState state) => new(
        ProgressOperations.MigrationRun,
        state == ProgressState.Completed ? ProgressActions.RunCompleted : ProgressActions.RunFailed,
        state,
        "Integration run",
        state.ToString(),
        "Review");

    private static void RequireConfiguredConnection()
    {
        if (string.Equals(
            System.Environment.GetEnvironmentVariable(RequiredVariable), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{ConnectionVariable} is required when {RequiredVariable}=true.");
        }
    }

    private static WorkbenchActor Actor(string objectId) =>
        WorkbenchActor.ForTenant(
            Tenant, objectId, [WorkbenchRoles.MigrationOperator, WorkbenchRoles.SandboxApprover]);

    private static PlatformTargetProfileEnvironment TargetEnvironment() => new()
    {
        AzureTenantId = Tenant,
        SubscriptionId = "4d1a0e6f-9b77-4b5e-a0ef-2c7d6a41f8b2",
        ResourceGroup = "rg-postgres-integration",
        ResourceId = "/subscriptions/4d1a0e6f/resourceGroups/rg-postgres-integration/providers/Microsoft.DBforPostgreSQL/flexibleServers/pg-sandbox",
        Region = "eastus2",
        SchemaName = "public",
        EnvironmentName = "integration",
    };
}