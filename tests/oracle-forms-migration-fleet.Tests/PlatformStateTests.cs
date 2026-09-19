// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Hosting;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// Persisted platform state and the approval state machine, against the durable local adapter.
///
/// Everything here writes to a real temporary file and reads it back through a freshly constructed
/// store, because the property being tested is durability: an approval that only exists in a field is
/// indistinguishable from one that does, right up to the restart that loses it. No Azure resource is
/// contacted and no connection is opened.
/// </summary>
public class PlatformStateTests : IDisposable
{
    private const string Tenant = "8f1e2b42-6a1d-4a24-9ad2-6c1b6a8f3d71";
    private const string OtherTenant = "11111111-2222-3333-4444-555555555555";
    private const string Requester = "3b4c9a10-7d42-4f0e-9d51-2a61f0c4b8e3";
    private const string Approver = "9c2d7e51-0b83-4a6f-8c19-5d7e2f1a4b60";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ofm-platform-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string StatePath(string name = "platform-state.json") => Path.Combine(_root, name);

    private FilePlatformStateStore Store(string name = "platform-state.json") => new(StatePath(name));

    private static readonly ConfiguredSandboxTargetBinding Sandbox =
        new("pg-sandbox.postgres.database.azure.com", "ofm_sandbox", "id-ofmfleet-web-dev", CanWrite: true);

    private static readonly PlatformTargetProfileEnvironment Environment = new()
    {
        AzureTenantId = Tenant,
        SubscriptionId = "4d1a0e6f-9b77-4b5e-a0ef-2c7d6a41f8b2",
        ResourceGroup = "rg-oracle-forms-migration-fleet-dev",
        ResourceId = "/subscriptions/4d1a0e6f/resourceGroups/rg-dev/providers/Microsoft.DBforPostgreSQL/flexibleServers/pg-sandbox",
        Region = "eastus2",
        SchemaName = "public",
        EnvironmentName = "sandbox",
    };

    private static WorkbenchActor Actor(string objectId, string tenant = Tenant, params string[] roles) =>
        WorkbenchActor.ForTenant(tenant, objectId, roles.Length == 0
            ? [WorkbenchRoles.MigrationOperator, WorkbenchRoles.SandboxApprover]
            : roles);

    private PlatformAccessService Service(
        IPlatformStateStore? store = null,
        ISandboxTargetBinding? sandbox = null,
        Func<DateTimeOffset>? clock = null) =>
        new(store ?? Store(), sandbox ?? Sandbox, clock);

    /// <summary>A project with a target profile and two members, which is the shape every approval needs.</summary>
    private async Task<(IPlatformStateStore Store, PlatformAccessService Service, string ProjectId, PlatformTargetProfile Profile)>
        SeedAsync(IPlatformStateStore? store = null, Func<DateTimeOffset>? clock = null)
    {
        store ??= Store();
        await store.InitializeAsync(CancellationToken.None);

        PlatformAccessService service = Service(store, clock: clock);
        PlatformResult<PlatformProject> project = await service.CreateProjectAsync(Actor(Requester), "ORDERS migration", CancellationToken.None);
        Assert.True(project.Succeeded, project.Error);

        PlatformResult<PlatformTargetProfile> profile = await service.EnsureConfiguredTargetProfileAsync(
            Actor(Requester), project.Value!.ProjectId, Environment, CancellationToken.None);
        Assert.True(profile.Succeeded, profile.Error);

        await store.UpsertMembershipAsync(
            new PlatformMembership
            {
                ProjectId = project.Value.ProjectId,
                TenantId = Tenant,
                ObjectId = Approver,
                Roles = [WorkbenchRoles.MigrationOperator, WorkbenchRoles.SandboxApprover],
                CreatedUtc = DateTimeOffset.UtcNow,
            },
            expectedVersion: null,
            CancellationToken.None);

        return (store, service, project.Value.ProjectId, profile.Value!);
    }

    private static PlatformApprovalRequestInput RequestInput(
        string projectId,
        string source = "a1b2c3",
        string plan = "d4e5f6",
        WorkbenchMutationScope scope = WorkbenchMutationScope.SandboxDatabaseWrite) =>
        new(projectId, "sandbox", scope, "ENG-42", source, plan, TimeSpan.FromHours(1), "Load the sandbox.");

    // ---- durability ----

    [Fact]
    public async Task Initialization_is_idempotent_and_keeps_what_was_already_recorded()
    {
        (IPlatformStateStore store, PlatformAccessService service, string projectId, _) = await SeedAsync();

        await store.InitializeAsync(CancellationToken.None);
        await store.InitializeAsync(CancellationToken.None);

        Assert.NotNull(await store.GetProjectAsync(Tenant, projectId, CancellationToken.None));
        Assert.Single(await service.ProjectsAsync(Actor(Requester), CancellationToken.None));
    }

    [Fact]
    public async Task State_survives_a_restart_of_the_store()
    {
        (_, PlatformAccessService service, string projectId, _) = await SeedAsync();
        PlatformResult<PlatformApproval> requested =
            await service.RequestAsync(Actor(Requester), RequestInput(projectId), CancellationToken.None);
        Assert.True(requested.Succeeded, requested.Error);

        // A brand new adapter over the same file is the closest offline analogue of a replica restart.
        FilePlatformStateStore restarted = Store();
        await restarted.InitializeAsync(CancellationToken.None);

        PlatformApproval? readBack = await restarted.GetApprovalAsync(Tenant, requested.Value!.ApprovalId, CancellationToken.None);

        Assert.NotNull(readBack);
        Assert.Equal(PlatformApprovalState.Requested, readBack.State);
        Assert.Equal(requested.Value.SourceSnapshotHash, readBack.SourceSnapshotHash);
    }

    [Fact]
    public async Task Two_stores_with_different_paths_share_nothing()
    {
        (_, _, string projectId, _) = await SeedAsync();

        FilePlatformStateStore other = Store("other-state.json");
        await other.InitializeAsync(CancellationToken.None);

        Assert.Null(await other.GetProjectAsync(Tenant, projectId, CancellationToken.None));
        Assert.NotEqual(Store().StatePath, other.StatePath);
        Assert.True(File.Exists(other.StatePath));
    }

    [Fact]
    public async Task Concurrent_writers_against_one_file_do_not_lose_a_record()
    {
        FilePlatformStateStore store = Store();
        await store.InitializeAsync(CancellationToken.None);
        PlatformAccessService service = Service(store);

        await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
            service.CreateProjectAsync(Actor(Requester), $"project-{index}", CancellationToken.None)));

        Assert.Equal(12, (await service.ProjectsAsync(Actor(Requester), CancellationToken.None)).Count);
    }

    [Fact]
    public async Task A_deployed_member_object_id_is_stored_in_canonical_guid_form()
    {
        (IPlatformStateStore store, PlatformAccessService service, string projectId, _) = await SeedAsync();
        const string member = "AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE";

        PlatformResult<PlatformMembership> added = await service.AddMemberAsync(
            Actor(Requester), projectId, member, [WorkbenchRoles.MigrationOperator], CancellationToken.None);

        Assert.True(added.Succeeded, added.Error);
        Assert.Equal(member.ToLowerInvariant(), added.Value!.ObjectId);
        Assert.NotNull(await store.GetMembershipAsync(Tenant, projectId, member.ToLowerInvariant(), CancellationToken.None));
    }

    [Fact]
    public async Task A_late_file_store_insert_does_not_overwrite_an_existing_membership()
    {
        (IPlatformStateStore store, _, string projectId, _) = await SeedAsync();
        PlatformMembership existing = (await store.GetMembershipAsync(Tenant, projectId, Approver, CancellationToken.None))!;

        PlatformMembership? overwritten = await store.UpsertMembershipAsync(
            existing with { Roles = [WorkbenchRoles.MigrationOperator] },
            expectedVersion: null,
            CancellationToken.None);

        Assert.Null(overwritten);
        PlatformMembership retained = (await store.GetMembershipAsync(Tenant, projectId, Approver, CancellationToken.None))!;
        Assert.True(retained.HasRole(WorkbenchRoles.SandboxApprover));
    }

    // ---- target profiles ----

    [Fact]
    public async Task A_target_profile_is_derived_from_configuration_and_never_from_a_caller()
    {
        (_, _, _, PlatformTargetProfile profile) = await SeedAsync();

        Assert.Equal(Sandbox.EndpointHost, profile.EndpointHost);
        Assert.Equal(Sandbox.DatabaseName, profile.DatabaseName);
        Assert.Equal(Sandbox.ExecutionIdentity, profile.ExecutionIdentity);
        Assert.Equal(1, profile.Version);
        Assert.Equal(64, profile.CanonicalHash.Length);
    }

    [Fact]
    public async Task Requesting_the_same_profile_again_returns_the_same_immutable_version()
    {
        (_, PlatformAccessService service, string projectId, PlatformTargetProfile first) = await SeedAsync();

        PlatformResult<PlatformTargetProfile> again = await service.EnsureConfiguredTargetProfileAsync(
            Actor(Requester), projectId, Environment, CancellationToken.None);

        Assert.True(again.Succeeded, again.Error);
        Assert.Equal(first.Version, again.Value!.Version);
        Assert.Equal(first.CanonicalHash, again.Value.CanonicalHash);
    }

    [Fact]
    public async Task A_different_target_produces_a_new_version_rather_than_an_edit()
    {
        (IPlatformStateStore store, _, string projectId, PlatformTargetProfile first) = await SeedAsync();

        PlatformAccessService moved = Service(
            store,
            new ConfiguredSandboxTargetBinding("pg-other.postgres.database.azure.com", "ofm_sandbox", "id-ofmfleet-web-dev", true));

        PlatformResult<PlatformTargetProfile> second = await moved.EnsureConfiguredTargetProfileAsync(
            Actor(Requester), projectId, Environment, CancellationToken.None);

        Assert.True(second.Succeeded, second.Error);
        Assert.Equal(2, second.Value!.Version);
        Assert.NotEqual(first.CanonicalHash, second.Value.CanonicalHash);

        // Version 1 is still exactly what it was, so a grant issued against it still describes a real target.
        PlatformTargetProfile? original = await store.GetTargetProfileAsync(Tenant, projectId, "sandbox", 1, CancellationToken.None);
        Assert.Equal(first.CanonicalHash, original!.CanonicalHash);
        Assert.Equal(Sandbox.EndpointHost, original.EndpointHost);
    }

    [Fact]
    public async Task Repeated_configuration_changes_create_only_one_version_per_distinct_target()
    {
        (IPlatformStateStore store, PlatformAccessService original, string projectId, PlatformTargetProfile first) =
            await SeedAsync();
        PlatformAccessService moved = Service(
            store,
            new ConfiguredSandboxTargetBinding("pg-other.postgres.database.azure.com", "ofm_sandbox", "id-ofmfleet-web-dev", true));

        PlatformResult<PlatformTargetProfile>[] profiles =
        [
            PlatformResult<PlatformTargetProfile>.Ok(first),
            await original.EnsureConfiguredTargetProfileAsync(Actor(Requester), projectId, Environment, CancellationToken.None),
            await moved.EnsureConfiguredTargetProfileAsync(Actor(Requester), projectId, Environment, CancellationToken.None),
            await moved.EnsureConfiguredTargetProfileAsync(Actor(Requester), projectId, Environment, CancellationToken.None),
            await moved.EnsureConfiguredTargetProfileAsync(Actor(Requester), projectId, Environment, CancellationToken.None),
        ];

        Assert.All(profiles, profile => Assert.True(profile.Succeeded, profile.Error));
        Assert.Equal([1, 1, 2, 2, 2], profiles.Select(profile => profile.Value!.Version));
        Assert.NotEqual(profiles[0].Value!.CanonicalHash, profiles[2].Value!.CanonicalHash);
        Assert.Null(await store.GetTargetProfileAsync(Tenant, projectId, "sandbox", 3, CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_equivalent_target_changes_return_the_same_immutable_version()
    {
        (IPlatformStateStore store, _, string projectId, _) = await SeedAsync();
        RacingTargetProfileStore racing = new(store);
        ConfiguredSandboxTargetBinding changed =
            new("pg-other.postgres.database.azure.com", "ofm_sandbox", "id-ofmfleet-web-dev", true);

        PlatformResult<PlatformTargetProfile>[] results = await Task.WhenAll(
            Service(racing, changed).EnsureConfiguredTargetProfileAsync(
                Actor(Requester), projectId, Environment, CancellationToken.None),
            Service(racing, changed).EnsureConfiguredTargetProfileAsync(
                Actor(Requester), projectId, Environment, CancellationToken.None));

        Assert.All(results, result => Assert.True(result.Succeeded, result.Error));
        Assert.All(results, result => Assert.Equal(2, result.Value!.Version));
        Assert.Equal(results[0].Value!.CanonicalHash, results[1].Value!.CanonicalHash);
        Assert.Null(await store.GetTargetProfileAsync(Tenant, projectId, "sandbox", 3, CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_distinct_target_changes_receive_distinct_immutable_versions()
    {
        (IPlatformStateStore store, _, string projectId, _) = await SeedAsync();
        RacingTargetProfileStore racing = new(store);

        PlatformResult<PlatformTargetProfile>[] results = await Task.WhenAll(
            Service(racing, Sandbox with { EndpointHost = "pg-a.postgres.database.azure.com" })
                .EnsureConfiguredTargetProfileAsync(Actor(Requester), projectId, Environment, CancellationToken.None),
            Service(racing, Sandbox with { EndpointHost = "pg-b.postgres.database.azure.com" })
                .EnsureConfiguredTargetProfileAsync(Actor(Requester), projectId, Environment, CancellationToken.None));

        Assert.All(results, result => Assert.True(result.Succeeded, result.Error));
        Assert.Equal([2, 3], results.Select(result => result.Value!.Version).Order());
        Assert.Equal(
            ["pg-a.postgres.database.azure.com", "pg-b.postgres.database.azure.com"],
            results.Select(result => result.Value!.EndpointHost).Order());
        Assert.NotEqual(results[0].Value!.CanonicalHash, results[1].Value!.CanonicalHash);
    }

    [Theory]
    [InlineData("postgres://user:hunter2@host")]
    [InlineData("host:5432")]
    [InlineData("operator@pg-sandbox")]
    public void A_target_profile_refuses_an_endpoint_that_is_not_a_bare_host_name(string endpoint)
    {
        PlatformTargetProfile profile = new()
        {
            TargetProfileId = "sandbox",
            ProjectId = "prj-1",
            TenantId = Tenant,
            Version = 1,
            AzureTenantId = Tenant,
            SubscriptionId = "sub",
            ResourceGroup = "rg",
            ResourceId = "/subscriptions/sub/rg",
            Region = "eastus2",
            EndpointHost = endpoint,
            DatabaseName = "ofm",
            SchemaName = "public",
            ExecutionIdentity = "identity",
            EnvironmentName = "sandbox",
            StackDatabase = "PostgreSql",
            StackFrontEnd = "React",
            StackBackEnd = "JavaSpringBoot",
            CanonicalHash = "hash",
            CreatedUtc = DateTimeOffset.UtcNow,
        };

        Assert.False(PlatformTargetProfiles.TryValidate(profile, out PlatformTargetProfileRejection? rejection));
        Assert.NotEqual(string.Empty, rejection!.Reason);
    }

    [Fact]
    public void A_target_profile_hash_covers_the_azure_coordinates_and_stack_choice()
    {
        PlatformTargetProfile profile = new()
        {
            TargetProfileId = "sandbox",
            ProjectId = "prj-1",
            TenantId = Tenant,
            Version = 1,
            AzureTenantId = Tenant,
            SubscriptionId = "sub",
            ResourceGroup = "rg",
            ResourceId = "/subscriptions/sub/rg",
            Region = "eastus2",
            EndpointHost = "pg-sandbox.postgres.database.azure.com",
            DatabaseName = "ofm",
            SchemaName = "public",
            ExecutionIdentity = "identity",
            EnvironmentName = "sandbox",
            StackDatabase = "PostgreSql",
            StackFrontEnd = "React",
            StackBackEnd = "JavaSpringBoot",
            CanonicalHash = string.Empty,
            CreatedUtc = DateTimeOffset.UtcNow,
        };

        Assert.NotEqual(
            PlatformTargetProfiles.Hash(profile),
            PlatformTargetProfiles.Hash(profile with { StackFrontEnd = "Angular", StackBackEnd = "DotNet" }));

        Assert.NotEqual(
            PlatformTargetProfiles.Hash(profile),
            PlatformTargetProfiles.Hash(profile with { DatabaseName = "ofm_other" }));

        Assert.NotEqual(
            PlatformTargetProfiles.Hash(profile),
            PlatformTargetProfiles.Hash(profile with { Version = 2 }));
    }

    // ---- approval state machine ----

    [Fact]
    public async Task A_request_is_approved_by_a_different_member_and_recorded_with_who_and_when()
    {
        (_, PlatformAccessService service, string projectId, PlatformTargetProfile profile) = await SeedAsync();

        PlatformApproval requested =
            (await service.RequestAsync(Actor(Requester), RequestInput(projectId), CancellationToken.None)).Value!;

        Assert.Equal(PlatformApprovalState.Requested, requested.State);
        Assert.Equal(profile.CanonicalHash, requested.TargetProfileHash);
        Assert.Equal(profile.Version, requested.TargetProfileVersion);

        PlatformResult<PlatformApproval> decided = await service.DecideAsync(
            Actor(Approver), requested.ApprovalId, approve: true, requested.Version, "Reviewed the plan.", CancellationToken.None);

        Assert.True(decided.Succeeded, decided.Error);
        Assert.Equal(PlatformApprovalState.Approved, decided.Value!.State);
        Assert.Equal(Approver, decided.Value.DecidedByObjectId);
        Assert.NotNull(decided.Value.DecidedUtc);
        Assert.Equal(requested.Version + 1, decided.Value.Version);
    }

    [Fact]
    public async Task A_requester_cannot_decide_their_own_request()
    {
        (_, PlatformAccessService service, string projectId, _) = await SeedAsync();
        PlatformApproval requested =
            (await service.RequestAsync(Actor(Requester), RequestInput(projectId), CancellationToken.None)).Value!;

        PlatformResult<PlatformApproval> decided = await service.DecideAsync(
            Actor(Requester), requested.ApprovalId, approve: true, requested.Version, null, CancellationToken.None);

        Assert.False(decided.Succeeded);
        Assert.Equal(403, decided.Status);
    }

    [Fact]
    public async Task Deciding_requires_the_sandbox_approver_role_in_that_project()
    {
        (IPlatformStateStore store, PlatformAccessService service, string projectId, _) = await SeedAsync();
        PlatformApproval requested =
            (await service.RequestAsync(Actor(Requester), RequestInput(projectId), CancellationToken.None)).Value!;

        PlatformMembership approver = (await store.GetMembershipAsync(Tenant, projectId, Approver, CancellationToken.None))!;
        await store.UpsertMembershipAsync(
            approver with { Roles = [WorkbenchRoles.MigrationOperator] }, approver.Version, CancellationToken.None);

        PlatformResult<PlatformApproval> decided = await service.DecideAsync(
            Actor(Approver), requested.ApprovalId, approve: true, requested.Version, null, CancellationToken.None);

        Assert.False(decided.Succeeded);
        Assert.Equal(403, decided.Status);
    }

    [Fact]
    public async Task A_stale_version_loses_the_race_rather_than_overwriting_the_decision()
    {
        (_, PlatformAccessService service, string projectId, _) = await SeedAsync();
        PlatformApproval requested =
            (await service.RequestAsync(Actor(Requester), RequestInput(projectId), CancellationToken.None)).Value!;

        Assert.True((await service.DecideAsync(
            Actor(Approver), requested.ApprovalId, approve: true, requested.Version, null, CancellationToken.None)).Succeeded);

        PlatformResult<PlatformApproval> second = await service.DecideAsync(
            Actor(Approver), requested.ApprovalId, approve: false, requested.Version, null, CancellationToken.None);

        Assert.False(second.Succeeded);
        Assert.Equal(409, second.Status);
    }

    [Fact]
    public async Task A_rejected_request_cannot_be_decided_again_and_never_becomes_a_grant()
    {
        (IPlatformStateStore store, PlatformAccessService service, string projectId, _) = await SeedAsync();
        PlatformApproval requested =
            (await service.RequestAsync(Actor(Requester), RequestInput(projectId), CancellationToken.None)).Value!;

        PlatformApproval rejected = (await service.DecideAsync(
            Actor(Approver), requested.ApprovalId, approve: false, requested.Version, "Not yet.", CancellationToken.None)).Value!;

        Assert.Equal(PlatformApprovalState.Rejected, rejected.State);
        Assert.Empty(await new PlatformAuthorizationStore(store, Sandbox)
            .ForOwnerAsync(Actor(Requester).OwnerId, CancellationToken.None));

        PlatformResult<PlatformApproval> again = await service.DecideAsync(
            Actor(Approver), requested.ApprovalId, approve: true, rejected.Version, null, CancellationToken.None);

        Assert.False(again.Succeeded);
        Assert.Equal(409, again.Status);
    }

    [Fact]
    public async Task Production_scope_is_refused_even_when_a_member_asks_for_it()
    {
        (_, PlatformAccessService service, string projectId, _) = await SeedAsync();

        PlatformResult<PlatformApproval> requested = await service.RequestAsync(
            Actor(Requester),
            RequestInput(projectId, scope: WorkbenchMutationScope.ProductionWrite),
            CancellationToken.None);

        Assert.False(requested.Succeeded);
        Assert.Equal(409, requested.Status);
        Assert.Contains("Production", requested.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validation_only_approval_is_persisted_but_never_projects_a_mutation_grant()
    {
        (IPlatformStateStore store, PlatformAccessService service, string projectId, _) = await SeedAsync();

        PlatformApproval requested = (await service.RequestAsync(
            Actor(Requester),
            RequestInput(projectId, scope: WorkbenchMutationScope.ValidationOnly),
            CancellationToken.None)).Value!;

        PlatformApproval approved = (await service.DecideAsync(
            Actor(Approver), requested.ApprovalId, approve: true, requested.Version, null, CancellationToken.None)).Value!;

        Assert.Equal(WorkbenchMutationScope.ValidationOnly, approved.Scope);
        Assert.True(approved.IsEffective(DateTimeOffset.UtcNow));
        Assert.NotNull(await store.GetApprovalAsync(Tenant, approved.ApprovalId, CancellationToken.None));
        Assert.Empty(await new PlatformAuthorizationStore(store, Sandbox)
            .ForOwnerAsync(Actor(Requester).OwnerId, CancellationToken.None));
    }

    [Fact]
    public async Task An_approval_request_must_bind_a_source_the_server_indexed()
    {
        (_, PlatformAccessService service, string projectId, _) = await SeedAsync();

        PlatformResult<PlatformApproval> noSource = await service.RequestAsync(
            Actor(Requester),
            RequestInput(projectId, source: WorkbenchTrustBoundary.NoSourceHash),
            CancellationToken.None);

        Assert.False(noSource.Succeeded);
        Assert.Equal(400, noSource.Status);
    }

    // ---- membership and tenancy ----

    [Fact]
    public async Task A_member_of_one_project_reaches_nothing_in_another()
    {
        (IPlatformStateStore store, PlatformAccessService service, string projectId, _) = await SeedAsync();
        PlatformProject other = (await service.CreateProjectAsync(Actor(Approver), "other", CancellationToken.None)).Value!;

        WorkbenchActor outsider = Actor("6f0a1b2c-3d4e-5f60-7a8b-9c0d1e2f3a4b");
        PlatformResult<PlatformMembership> access =
            await service.RequireMembershipAsync(outsider, projectId, null, CancellationToken.None);

        Assert.False(access.Succeeded);
        Assert.Equal(404, access.Status);

        // The requester founded only the first project, so the second is invisible to them too.
        Assert.Null(await store.GetMembershipAsync(Tenant, other.ProjectId, Requester, CancellationToken.None));
        Assert.DoesNotContain(
            await service.ProjectsAsync(Actor(Requester), CancellationToken.None),
            project => project.ProjectId == other.ProjectId);
    }

    [Fact]
    public async Task The_same_object_id_under_a_different_tenant_is_a_different_actor()
    {
        (_, PlatformAccessService service, string projectId, _) = await SeedAsync();

        PlatformResult<PlatformMembership> access = await service.RequireMembershipAsync(
            Actor(Requester, OtherTenant), projectId, null, CancellationToken.None);

        Assert.False(access.Succeeded);
        Assert.Equal(404, access.Status);
        Assert.Empty(await service.ProjectsAsync(Actor(Requester, OtherTenant), CancellationToken.None));
    }

    // ---- grant projection ----

    private async Task<(IPlatformStateStore Store, PlatformAccessService Service, string ProjectId, PlatformApproval Approval)>
        ApprovedAsync(Func<DateTimeOffset>? clock = null, string source = "a1b2c3", string plan = "d4e5f6")
    {
        (IPlatformStateStore store, PlatformAccessService service, string projectId, _) = await SeedAsync(clock: clock);

        PlatformApproval requested = (await service.RequestAsync(
            Actor(Requester), RequestInput(projectId, source, plan), CancellationToken.None)).Value!;

        PlatformApproval approved = (await service.DecideAsync(
            Actor(Approver), requested.ApprovalId, approve: true, requested.Version, null, CancellationToken.None)).Value!;

        return (store, service, projectId, approved);
    }

    [Fact]
    public async Task An_approved_request_becomes_a_grant_bound_to_exactly_what_was_approved()
    {
        (IPlatformStateStore store, _, string projectId, PlatformApproval approval) = await ApprovedAsync();

        IReadOnlyList<WorkbenchAuthorizationRecord> grants = await new PlatformAuthorizationStore(store, Sandbox)
            .ForOwnerAsync(Actor(Requester).OwnerId, CancellationToken.None);

        WorkbenchAuthorizationRecord grant = Assert.Single(grants);
        Assert.Equal(approval.ApprovalId, grant.AuthorizationId);
        Assert.Equal(Tenant, grant.TenantId);
        Assert.Equal(projectId, grant.ProjectId);
        Assert.Equal("sandbox", grant.TargetProfileId);
        Assert.Equal(approval.TargetProfileHash, grant.TargetHash);
        Assert.Equal(Approver, grant.ApprovedByObjectId);
        Assert.Equal(WorkbenchMutationScope.SandboxDatabaseWrite, grant.Scope);
    }

    [Fact]
    public async Task A_valid_grant_authorizes_the_exact_run_and_nothing_that_drifted_from_it()
    {
        (IPlatformStateStore store, _, string projectId, PlatformApproval approval) = await ApprovedAsync();
        WorkbenchAuthorizationService authorization = new(new PlatformAuthorizationStore(store, Sandbox));
        WorkbenchActor actor = Actor(Requester);

        WorkbenchAuthorizationQuery exact = new(
            actor, approval.EngagementId, approval.SourceSnapshotHash, approval.PlanInputHash,
            approval.TargetProfileHash, WorkbenchMutationScope.SandboxDatabaseWrite,
            Tenant, projectId, "sandbox", approval.TargetProfileVersion);

        Assert.True((await authorization.AuthorizeAsync(exact, DateTimeOffset.UtcNow)).IsAuthorized);

        Assert.False((await authorization.AuthorizeAsync(
            exact with { SourceSnapshotHash = "000000" }, DateTimeOffset.UtcNow)).IsAuthorized);
        Assert.False((await authorization.AuthorizeAsync(
            exact with { PlanInputHash = "000000" }, DateTimeOffset.UtcNow)).IsAuthorized);
        Assert.False((await authorization.AuthorizeAsync(
            exact with { TargetHash = new string('0', approval.TargetProfileHash.Length) }, DateTimeOffset.UtcNow)).IsAuthorized);
        Assert.False((await authorization.AuthorizeAsync(
            exact with { TargetProfileVersion = approval.TargetProfileVersion + 1 }, DateTimeOffset.UtcNow)).IsAuthorized);
        Assert.False((await authorization.AuthorizeAsync(
            exact with { ProjectId = "prj-somewhere-else" }, DateTimeOffset.UtcNow)).IsAuthorized);
        Assert.False((await authorization.AuthorizeAsync(
            exact with { EngagementId = "ENG-OTHER" }, DateTimeOffset.UtcNow)).IsAuthorized);
        Assert.False((await authorization.AuthorizeAsync(
            exact with { Scope = WorkbenchMutationScope.ProductionWrite }, DateTimeOffset.UtcNow)).IsAuthorized);
    }

    [Fact]
    public async Task Revocation_removes_the_grant_without_claiming_to_undo_anything()
    {
        (IPlatformStateStore store, PlatformAccessService service, _, PlatformApproval approval) = await ApprovedAsync();
        PlatformAuthorizationStore grants = new(store, Sandbox);

        Assert.Single(await grants.ForOwnerAsync(Actor(Requester).OwnerId, CancellationToken.None));

        PlatformResult<PlatformApproval> revoked = await service.RevokeAsync(
            Actor(Approver), approval.ApprovalId, approval.Version, "Window closed.", CancellationToken.None);

        Assert.True(revoked.Succeeded, revoked.Error);
        Assert.Equal(PlatformApprovalState.Revoked, revoked.Value!.State);
        Assert.Equal(Approver, revoked.Value.RevokedByObjectId);
        Assert.Empty(await grants.ForOwnerAsync(Actor(Requester).OwnerId, CancellationToken.None));

        // The record is still there. Revocation is a transition, not a deletion of the audit trail.
        Assert.NotNull(await store.GetApprovalAsync(Tenant, approval.ApprovalId, CancellationToken.None));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("database")]
    [InlineData("endpoint")]
    [InlineData("identity")]
    [InlineData("environment")]
    public async Task Superseding_any_destination_dimension_removes_the_old_grant_but_keeps_its_audit_record(
        string dimension)
    {
        (IPlatformStateStore store, _, string projectId, PlatformApproval approval) =
            await ApprovedAsync();
        PlatformTargetProfileEnvironment environment = dimension switch
        {
            "schema" => Environment with { SchemaName = "project_validation" },
            "environment" => Environment with { EnvironmentName = "validation" },
            _ => Environment,
        };
        ConfiguredSandboxTargetBinding sandbox = dimension switch
        {
            "database" => Sandbox with { DatabaseName = "ofm_sandbox_v2" },
            "endpoint" => Sandbox with { EndpointHost = "pg-other.postgres.database.azure.com" },
            "identity" => Sandbox with { ExecutionIdentity = "id-ofmfleet-web-v2" },
            _ => Sandbox,
        };
        PlatformAccessService service = Service(store, sandbox);

        PlatformResult<PlatformTargetProfile> superseding = await service.EnsureConfiguredTargetProfileAsync(
            Actor(Requester),
            projectId,
            environment,
            CancellationToken.None);

        Assert.True(superseding.Succeeded, superseding.Error);
        Assert.Equal(approval.TargetProfileVersion + 1, superseding.Value!.Version);
        Assert.Empty(await new PlatformAuthorizationStore(store, sandbox)
            .ForOwnerAsync(Actor(Requester).OwnerId, CancellationToken.None));

        PlatformApproval? historical = await store.GetApprovalAsync(Tenant, approval.ApprovalId, CancellationToken.None);
        Assert.NotNull(historical);
        Assert.Equal(PlatformApprovalState.Approved, historical.State);
        Assert.Equal(approval.TargetProfileVersion, historical.TargetProfileVersion);
        Assert.Equal(approval.TargetProfileHash, historical.TargetProfileHash);
    }

    [Fact]
    public async Task Superseding_the_profile_between_gateway_calls_blocks_the_next_external_operation()
    {
        (IPlatformStateStore store, PlatformAccessService service, string projectId, PlatformApproval approval) =
            await ApprovedAsync();
        MigrationRunRequest request = new()
        {
            EngagementId = approval.EngagementId,
            ApplicationName = "ORDERS",
            RequestedMode = ExecutionMode.SandboxMigration,
            Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
            SourceRoot = "forms",
            OutputRoot = "out",
        };
        WorkbenchMutationAuthorizer authorizer = new(
            new WorkbenchAuthorizationService(new PlatformAuthorizationStore(store, Sandbox)),
            Actor(Requester),
            approval.SourceSnapshotHash,
            approval.PlanInputHash,
            approval.TargetProfileHash,
            projectId: projectId,
            targetProfileId: approval.TargetProfileId,
            targetProfileVersion: approval.TargetProfileVersion);
        AuthorizingDataMigrationGateway gateway = new(
            new StubDataGateway(new DataMigrationOutcome(0, 0, [], [])), authorizer, request);

        Assert.Empty(await gateway.CountAsync([], CancellationToken.None));

        PlatformResult<PlatformTargetProfile> superseding = await service.EnsureConfiguredTargetProfileAsync(
            Actor(Requester),
            projectId,
            Environment with { EnvironmentName = "superseding" },
            CancellationToken.None);
        Assert.True(superseding.Succeeded, superseding.Error);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => gateway.CountAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task An_expired_approval_stops_being_a_grant_without_anyone_acting()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        (IPlatformStateStore store, _, _, _) = await ApprovedAsync(clock: () => now);

        PlatformAuthorizationStore grants = new(store, Sandbox, () => now);
        Assert.Single(await grants.ForOwnerAsync(Actor(Requester).OwnerId, CancellationToken.None));

        Assert.Empty(await new PlatformAuthorizationStore(store, Sandbox, () => now.AddHours(2))
            .ForOwnerAsync(Actor(Requester).OwnerId, CancellationToken.None));
    }

    [Fact]
    public async Task Removing_the_member_removes_the_grant_the_approval_still_records()
    {
        (IPlatformStateStore store, _, string projectId, _) = await ApprovedAsync();
        PlatformAuthorizationStore grants = new(store, Sandbox);

        Assert.Single(await grants.ForOwnerAsync(Actor(Requester).OwnerId, CancellationToken.None));

        PlatformMembership membership = (await store.GetMembershipAsync(Tenant, projectId, Requester, CancellationToken.None))!;
        await store.UpsertMembershipAsync(
            membership with { RemovedUtc = DateTimeOffset.UtcNow }, membership.Version, CancellationToken.None);

        Assert.Empty(await grants.ForOwnerAsync(Actor(Requester).OwnerId, CancellationToken.None));
    }

    [Fact]
    public async Task Losing_the_operator_role_removes_the_grant()
    {
        (IPlatformStateStore store, _, string projectId, _) = await ApprovedAsync();
        PlatformMembership membership = (await store.GetMembershipAsync(Tenant, projectId, Requester, CancellationToken.None))!;

        await store.UpsertMembershipAsync(
            membership with { Roles = [WorkbenchRoles.SandboxApprover] }, membership.Version, CancellationToken.None);

        Assert.Empty(await new PlatformAuthorizationStore(store, Sandbox)
            .ForOwnerAsync(Actor(Requester).OwnerId, CancellationToken.None));
    }

    /// <summary>
    /// The grant names a database. If the process is wired to a different one, honouring the grant would
    /// write approved changes to an unapproved target, so the grant stops existing instead.
    /// </summary>
    [Fact]
    public async Task A_grant_disappears_when_the_configured_sandbox_is_not_the_approved_target()
    {
        (IPlatformStateStore store, _, _, _) = await ApprovedAsync();

        Assert.Empty(await new PlatformAuthorizationStore(
                store,
                new ConfiguredSandboxTargetBinding("pg-elsewhere.postgres.database.azure.com", "ofm_sandbox", "id-ofmfleet-web-dev", true))
            .ForOwnerAsync(Actor(Requester).OwnerId, CancellationToken.None));

        Assert.Empty(await new PlatformAuthorizationStore(store, sandbox: null)
            .ForOwnerAsync(Actor(Requester).OwnerId, CancellationToken.None));
    }

    [Fact]
    public async Task An_approval_for_one_actor_is_not_a_grant_for_another()
    {
        (IPlatformStateStore store, _, _, _) = await ApprovedAsync();
        PlatformAuthorizationStore grants = new(store, Sandbox);

        Assert.Empty(await grants.ForOwnerAsync(Actor(Approver).OwnerId, CancellationToken.None));
        Assert.Empty(await grants.ForOwnerAsync(Actor(Requester, OtherTenant).OwnerId, CancellationToken.None));
        Assert.Empty(await grants.ForOwnerAsync("no-tenant-qualifier", CancellationToken.None));
    }

    private sealed class RacingTargetProfileStore(IPlatformStateStore inner) : IPlatformStateStore
    {
        private readonly TaskCompletionSource _bothCreates = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _createCalls;

        public string Description => inner.Description;

        public Task InitializeAsync(CancellationToken cancellationToken) => inner.InitializeAsync(cancellationToken);

        public Task<PlatformOrganization> EnsureOrganizationAsync(
            string tenantId, string displayName, CancellationToken cancellationToken) =>
            inner.EnsureOrganizationAsync(tenantId, displayName, cancellationToken);

        public Task<PlatformProject> CreateProjectAsync(
            PlatformProject project, PlatformMembership founder, CancellationToken cancellationToken) =>
            inner.CreateProjectAsync(project, founder, cancellationToken);

        public Task<PlatformProject?> GetProjectAsync(
            string tenantId, string projectId, CancellationToken cancellationToken) =>
            inner.GetProjectAsync(tenantId, projectId, cancellationToken);

        public Task<IReadOnlyList<PlatformProject>> ProjectsForActorAsync(
            string tenantId, string objectId, CancellationToken cancellationToken) =>
            inner.ProjectsForActorAsync(tenantId, objectId, cancellationToken);

        public Task<PlatformMembership?> GetMembershipAsync(
            string tenantId, string projectId, string objectId, CancellationToken cancellationToken) =>
            inner.GetMembershipAsync(tenantId, projectId, objectId, cancellationToken);

        public Task<IReadOnlyList<PlatformMembership>> MembershipsAsync(
            string tenantId, string projectId, CancellationToken cancellationToken) =>
            inner.MembershipsAsync(tenantId, projectId, cancellationToken);

        public Task<PlatformMembership?> UpsertMembershipAsync(
            PlatformMembership membership, int? expectedVersion, CancellationToken cancellationToken) =>
            inner.UpsertMembershipAsync(membership, expectedVersion, cancellationToken);

        public async Task<PlatformTargetProfile?> CreateTargetProfileAsync(
            PlatformTargetProfile profile, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _createCalls) == 2)
            {
                _bothCreates.TrySetResult();
            }

            await _bothCreates.Task.WaitAsync(cancellationToken);
            return await inner.CreateTargetProfileAsync(profile, cancellationToken);
        }

        public Task<PlatformTargetProfile?> GetTargetProfileAsync(
            string tenantId, string projectId, string targetProfileId, int? version, CancellationToken cancellationToken) =>
            inner.GetTargetProfileAsync(tenantId, projectId, targetProfileId, version, cancellationToken);

        public Task<IReadOnlyList<PlatformTargetProfile>> TargetProfilesAsync(
            string tenantId, string projectId, CancellationToken cancellationToken) =>
            inner.TargetProfilesAsync(tenantId, projectId, cancellationToken);

        public Task<PlatformApproval> CreateApprovalAsync(
            PlatformApproval approval, CancellationToken cancellationToken) =>
            inner.CreateApprovalAsync(approval, cancellationToken);

        public Task<PlatformApproval?> GetApprovalAsync(
            string tenantId, string approvalId, CancellationToken cancellationToken) =>
            inner.GetApprovalAsync(tenantId, approvalId, cancellationToken);

        public Task<IReadOnlyList<PlatformApproval>> ApprovalsForProjectAsync(
            string tenantId, string projectId, CancellationToken cancellationToken) =>
            inner.ApprovalsForProjectAsync(tenantId, projectId, cancellationToken);

        public Task<IReadOnlyList<PlatformApproval>> ApprovalsForRequesterAsync(
            string tenantId, string objectId, CancellationToken cancellationToken) =>
            inner.ApprovalsForRequesterAsync(tenantId, objectId, cancellationToken);

        public Task<PlatformApproval?> UpdateApprovalAsync(
            PlatformApproval approval, int expectedVersion, CancellationToken cancellationToken) =>
            inner.UpdateApprovalAsync(approval, expectedVersion, cancellationToken);
    }
}
