// Copyright (c) Microsoft. All rights reserved.

using Npgsql;
using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

public sealed class PostgresPlatformStateIntegrationTests : IAsyncLifetime
{
    private const string ConnectionVariable = "PLATFORM_POSTGRES_INTEGRATION_CONNECTION";
    private const string RequiredVariable = "REQUIRE_POSTGRES_INTEGRATION";
    private const string Tenant = "8f1e2b42-6a1d-4a24-9ad2-6c1b6a8f3d71";
    private const string Requester = "3b4c9a10-7d42-4f0e-9d51-2a61f0c4b8e3";
    private const string Approver = "9c2d7e51-0b83-4a6f-8c19-5d7e2f1a4b60";

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
        PlatformAccessService platform = new(store, sandbox);
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
                "Integration request"),
            CancellationToken.None)).Value!;
        PlatformApproval approved = (await platform.DecideAsync(
            approver, requested.ApprovalId, approve: true, requested.Version, "Approved", CancellationToken.None)).Value!;
        Assert.Single(await store.ApprovalsForProjectAsync(Tenant, project.ProjectId, CancellationToken.None));
        Assert.Single(await store.ApprovalsForRequesterAsync(Tenant, Requester, CancellationToken.None));

        Assert.Single(await new PlatformAuthorizationStore(store, sandbox)
            .ForOwnerAsync(requester.OwnerId, CancellationToken.None));

        PlatformApproval revoked = (await platform.RevokeAsync(
            requester, approved.ApprovalId, approved.Version, "Complete", CancellationToken.None)).Value!;
        Assert.Equal(PlatformApprovalState.Revoked, revoked.State);
        Assert.Empty(await new PlatformAuthorizationStore(store, sandbox)
            .ForOwnerAsync(requester.OwnerId, CancellationToken.None));
        Assert.Equal(
            ["Requested", "Approved", "Revoked"],
            await ApprovalEventsAsync(approved.ApprovalId));

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

    private PostgresPlatformStateStore? CreateStore()
    {
        if (_connectionString is null)
        {
            return null;
        }

        NpgsqlConnectionStringBuilder builder = new(_connectionString);
        if (string.IsNullOrWhiteSpace(builder.Host) ||
            string.IsNullOrWhiteSpace(builder.Database) ||
            string.IsNullOrWhiteSpace(builder.Username))
        {
            throw new InvalidOperationException("The PostgreSQL integration connection must name a host, database, and user.");
        }

        return new PostgresPlatformStateStore(
            new PlatformDatabaseOptions
            {
                Host = builder.Host!,
                Database = builder.Database,
                User = builder.Username,
                Schema = _schema,
            },
            OpenAsync);
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