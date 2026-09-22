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

    /// <summary>The stack the configured profile records, which is what a compatible request names.</summary>
    private static TargetStack ProfileTarget => new()
    {
        Database = DatabaseTarget.PostgreSql,
        FrontEnd = FrontEndStack.React,
        BackEnd = BackEndStack.JavaSpringBoot,
    };

    private static PlatformApprovalRequestInput RequestInput(
        string projectId,
        string source = "a1b2c3",
        string plan = "d4e5f6",
        WorkbenchMutationScope scope = WorkbenchMutationScope.SandboxDatabaseWrite,
        TargetStack? target = null) =>
        new(projectId, "sandbox", scope, "ENG-42", source, plan, TimeSpan.FromHours(1), "Load the sandbox.",
            target ?? ProfileTarget);

    // ---- the generated stack is a server-owned part of the target identity ----

    [Fact]
    public async Task A_deployment_that_declares_no_back_end_stack_still_records_the_java_one_it_was_planned_as()
    {
        (_, _, _, PlatformTargetProfile profile) = await SeedAsync();

        Assert.Equal(nameof(BackEndStack.JavaSpringBoot), profile.StackBackEnd);
        Assert.Equal(nameof(BackEndStack.JavaSpringBoot), Environment.StackBackEnd);
    }

    [Fact]
    public async Task A_dotnet_deployment_records_a_different_target_identity_than_the_java_one()
    {
        IPlatformStateStore store = Store();
        await store.InitializeAsync(CancellationToken.None);
        PlatformAccessService service = Service(store);
        string projectId = (await service.CreateProjectAsync(
            Actor(Requester), "ORDERS migration", CancellationToken.None)).Value!.ProjectId;

        PlatformResult<PlatformTargetProfile> java = await service.EnsureConfiguredTargetProfileAsync(
            Actor(Requester), projectId, Environment, CancellationToken.None);
        PlatformResult<PlatformTargetProfile> dotnet = await service.EnsureConfiguredTargetProfileAsync(
            Actor(Requester),
            projectId,
            Environment with { StackBackEnd = "aspnetcore" },
            CancellationToken.None);

        Assert.True(dotnet.Succeeded, dotnet.Error);
        Assert.Equal(nameof(BackEndStack.AspNetCore), dotnet.Value!.StackBackEnd);

        // A different generated application is a different destination, so it is a new immutable version
        // with its own canonical hash, never an edit of the one approvals were bound to.
        Assert.Equal(java.Value!.Version + 1, dotnet.Value.Version);
        Assert.NotEqual(java.Value.CanonicalHash, dotnet.Value.CanonicalHash);
    }

    [Fact]
    public async Task A_back_end_stack_this_build_cannot_generate_records_no_target_at_all()
    {
        IPlatformStateStore store = Store();
        await store.InitializeAsync(CancellationToken.None);
        PlatformAccessService service = Service(store);
        string projectId = (await service.CreateProjectAsync(
            Actor(Requester), "ORDERS migration", CancellationToken.None)).Value!.ProjectId;

        foreach (string declared in new[] { "NodeExpress", "1", "", "   " })
        {
            PlatformResult<PlatformTargetProfile> refused = await service.EnsureConfiguredTargetProfileAsync(
                Actor(Requester),
                projectId,
                Environment with { StackBackEnd = declared },
                CancellationToken.None);

            Assert.False(refused.Succeeded);
            Assert.Equal(409, refused.Status);

            // The refusal names the variable to set, never the value that was refused.
            if (declared.Trim().Length > 0)
            {
                Assert.DoesNotContain(declared.Trim(), refused.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Contains(
                PlatformTargetProfileEnvironment.BackEndVariable, refused.Error ?? string.Empty, StringComparison.Ordinal);
        }

        Assert.Null(await store.GetTargetProfileAsync(
            Tenant, projectId, "sandbox", version: null, CancellationToken.None));
    }

    /// <summary>
    /// QA finding: the request body chose the target stack and nothing compared it to the profile the
    /// approval was about to be bound to. The plan-input hash could not catch it, because an approval
    /// requested for the wrong stack hashes the wrong stack consistently.
    /// </summary>
    [Fact]
    public async Task An_approval_naming_a_stack_the_profile_does_not_is_refused_and_records_nothing()
    {
        (IPlatformStateStore store, PlatformAccessService service, string projectId, PlatformTargetProfile profile) =
            await SeedAsync();

        TargetStack[] mismatched =
        [
            ProfileTarget with { BackEnd = BackEndStack.AspNetCore },
            ProfileTarget with { Database = DatabaseTarget.AzureSqlDatabase },
            ProfileTarget with { Database = DatabaseTarget.Undetermined },
            ProfileTarget with { BackEnd = (BackEndStack)999 },
        ];

        foreach (TargetStack target in mismatched)
        {
            PlatformResult<PlatformApproval> refused = await service.RequestAsync(
                Actor(Requester), RequestInput(projectId, target: target), CancellationToken.None);

            Assert.False(refused.Succeeded);
            Assert.Equal(409, refused.Status);
            Assert.Empty(await store.ApprovalsForProjectAsync(Tenant, projectId, CancellationToken.None));
        }

        // A request naming the profile's own stack is still approved, so the check refuses the mismatch
        // and not the request.
        Assert.Equal(nameof(BackEndStack.JavaSpringBoot), profile.StackBackEnd);
        Assert.True((await service.RequestAsync(
            Actor(Requester), RequestInput(projectId), CancellationToken.None)).Succeeded);
        Assert.Single(await store.ApprovalsForProjectAsync(Tenant, projectId, CancellationToken.None));
    }

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
    public async Task One_shared_sandbox_accepts_database_write_requests_from_only_one_project()
    {
        (IPlatformStateStore store, PlatformAccessService service, string firstProject, _) = await SeedAsync();
        PlatformResult<PlatformApproval> first = await service.RequestAsync(
            Actor(Requester), RequestInput(firstProject), CancellationToken.None);
        Assert.True(first.Succeeded, first.Error);

        PlatformProject secondProject = (await service.CreateProjectAsync(
            Actor(Requester), "Second migration", CancellationToken.None)).Value!;
        Assert.True((await service.EnsureConfiguredTargetProfileAsync(
            Actor(Requester), secondProject.ProjectId, Environment, CancellationToken.None)).Succeeded);

        PlatformResult<PlatformApproval> denied = await service.RequestAsync(
            Actor(Requester), RequestInput(secondProject.ProjectId), CancellationToken.None);
        Assert.False(denied.Succeeded);
        Assert.Equal(409, denied.Status);
        Assert.Contains("one shared sandbox database", denied.Error, StringComparison.OrdinalIgnoreCase);

        PlatformResult<PlatformApproval> validation = await service.RequestAsync(
            Actor(Requester),
            RequestInput(secondProject.ProjectId, scope: WorkbenchMutationScope.ValidationOnly),
            CancellationToken.None);
        Assert.True(validation.Succeeded, validation.Error);

        PlatformAccessService restarted = Service(new FilePlatformStateStore(StatePath()));
        PlatformResult<PlatformApproval> stillDenied = await restarted.RequestAsync(
            Actor(Requester), RequestInput(secondProject.ProjectId), CancellationToken.None);
        Assert.False(stillDenied.Succeeded);
        Assert.Equal(409, stillDenied.Status);
    }

    [Fact]
    public async Task Concurrent_projects_cannot_both_claim_the_shared_sandbox()
    {
        (IPlatformStateStore store, PlatformAccessService service, string firstProject, _) = await SeedAsync();
        PlatformProject secondProject = (await service.CreateProjectAsync(
            Actor(Requester), "Second migration", CancellationToken.None)).Value!;
        Assert.True((await service.EnsureConfiguredTargetProfileAsync(
            Actor(Requester), secondProject.ProjectId, Environment, CancellationToken.None)).Succeeded);

        PlatformResult<PlatformApproval>[] results = await Task.WhenAll(
            service.RequestAsync(Actor(Requester), RequestInput(firstProject), CancellationToken.None),
            service.RequestAsync(Actor(Requester), RequestInput(secondProject.ProjectId), CancellationToken.None));

        Assert.Single(results, result => result.Succeeded);
        Assert.Single(results, result => !result.Succeeded && result.Status == 409);
        Assert.Single(await store.ApprovalsForRequesterAsync(Tenant, Requester, CancellationToken.None));
    }

    [Fact]
    public async Task Legacy_approvals_from_a_non_owner_project_cannot_be_decided_or_projected_as_grants()
    {
        (IPlatformStateStore store, PlatformAccessService service, string ownerProject, _) = await SeedAsync();
        PlatformApproval ownerApproval = (await service.RequestAsync(
            Actor(Requester), RequestInput(ownerProject), CancellationToken.None)).Value!;
        ownerApproval = (await service.DecideAsync(
            Actor(Approver), ownerApproval.ApprovalId, true, ownerApproval.Version, null, CancellationToken.None)).Value!;

        PlatformProject otherProject = (await service.CreateProjectAsync(
            Actor(Requester), "Legacy other project", CancellationToken.None)).Value!;
        PlatformTargetProfile otherProfile = (await service.EnsureConfiguredTargetProfileAsync(
            Actor(Requester), otherProject.ProjectId, Environment, CancellationToken.None)).Value!;
        Assert.True((await service.AddMemberAsync(
            Actor(Requester),
            otherProject.ProjectId,
            Approver,
            [WorkbenchRoles.MigrationOperator, WorkbenchRoles.SandboxApprover],
            CancellationToken.None)).Succeeded);
        PlatformApproval legacyPending = ownerApproval with
        {
            ApprovalId = $"apr-{Guid.NewGuid():N}",
            ProjectId = otherProject.ProjectId,
            State = PlatformApprovalState.Requested,
            TargetProfileVersion = otherProfile.Version,
            TargetProfileHash = otherProfile.CanonicalHash,
            RequestedUtc = ownerApproval.RequestedUtc.AddMinutes(1),
            DecidedByObjectId = null,
            DecidedUtc = null,
            Version = 1,
        };
        await store.CreateApprovalAsync(legacyPending, CancellationToken.None);

        PlatformResult<PlatformApproval> denied = await service.DecideAsync(
            Actor(Approver), legacyPending.ApprovalId, true, legacyPending.Version, null, CancellationToken.None);
        Assert.False(denied.Succeeded);
        Assert.Equal(409, denied.Status);

        PlatformApproval legacyApproved = legacyPending with
        {
            ApprovalId = $"apr-{Guid.NewGuid():N}",
            State = PlatformApprovalState.Approved,
            DecidedByObjectId = Approver,
            DecidedUtc = DateTimeOffset.UtcNow,
        };
        await store.CreateApprovalAsync(legacyApproved, CancellationToken.None);

        WorkbenchAuthorizationRecord grant = Assert.Single(
            await new PlatformAuthorizationStore(store, Sandbox)
                .ForOwnerAsync(Actor(Requester).OwnerId, CancellationToken.None));
        Assert.Equal(ownerApproval.ApprovalId, grant.AuthorizationId);
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
    [InlineData("backend")]
    public async Task Superseding_any_destination_dimension_removes_the_old_grant_but_keeps_its_audit_record(
        string dimension)
    {
        (IPlatformStateStore store, _, string projectId, PlatformApproval approval) =
            await ApprovedAsync();
        PlatformTargetProfileEnvironment environment = dimension switch
        {
            "schema" => Environment with { SchemaName = "project_validation" },
            "environment" => Environment with { EnvironmentName = "validation" },
            "backend" => Environment with { StackBackEnd = nameof(BackEndStack.AspNetCore) },
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

    /// <summary>
    /// A set of ledger rows that describe one indivisible fact is written together or not at all.
    ///
    /// The losing row is the middle one on purpose: a loop would already have written the row before it
    /// by the time it discovered the conflict, which is the half-recorded generation this exists to stop.
    /// </summary>
    [Fact]
    public async Task A_batch_of_ledger_updates_that_loses_one_version_race_writes_none_of_them()
    {
        IPlatformStateStore store = Store();
        await store.InitializeAsync(CancellationToken.None);
        IReadOnlyList<DispositionLedgerEntry> seeded = await LedgerAsync(store, "dled-batch");

        // Somebody else moved the middle row after these three versions were read.
        DispositionLedgerEntry moved = (await store.UpdateDispositionLedgerEntryAsync(
            seeded[1] with { Rationale = "Decided elsewhere." }, seeded[1].Version, CancellationToken.None))!;

        DispositionGeneratedReference reference = new(
            "run-batch", ".fleet-run/out/application", new string('d', 64), DateTimeOffset.UtcNow, new string('e', 64), 7);

        IReadOnlyList<DispositionLedgerEntry>? written = await store.UpdateDispositionLedgerEntriesAsync(
            [.. seeded.Select(entry => new DispositionLedgerEntryUpdate(
                entry with { GeneratedRefs = [reference] }, entry.Version))],
            CancellationToken.None);

        Assert.Null(written);

        IReadOnlyList<DispositionLedgerEntry> stored = await Reread(store, "dled-batch");
        Assert.All(stored, entry => Assert.Empty(entry.GeneratedRefs));
        Assert.Equal(seeded[0].Version, stored.Single(entry => entry.EntryId == seeded[0].EntryId).Version);
        Assert.Equal(seeded[2].Version, stored.Single(entry => entry.EntryId == seeded[2].EntryId).Version);
        Assert.Equal(moved.Version, stored.Single(entry => entry.EntryId == moved.EntryId).Version);
    }

    [Fact]
    public async Task A_batch_of_ledger_updates_that_wins_every_version_race_advances_every_row_once()
    {
        IPlatformStateStore store = Store();
        await store.InitializeAsync(CancellationToken.None);
        IReadOnlyList<DispositionLedgerEntry> seeded = await LedgerAsync(store, "dled-batch-ok");

        DispositionGeneratedReference reference = new(
            "run-batch", ".fleet-run/out/application", new string('e', 64), DateTimeOffset.UtcNow, new string('f', 64), 3);

        IReadOnlyList<DispositionLedgerEntry>? written = await store.UpdateDispositionLedgerEntriesAsync(
            [.. seeded.Select(entry => new DispositionLedgerEntryUpdate(
                entry with { GeneratedRefs = [reference] }, entry.Version))],
            CancellationToken.None);

        Assert.NotNull(written);
        Assert.Equal(seeded.Count, written.Count);

        IReadOnlyList<DispositionLedgerEntry> stored = await Reread(store, "dled-batch-ok");
        Assert.All(stored, entry => Assert.Equal(reference, entry.GeneratedRefs.Single()));
        Assert.All(stored, entry => Assert.Equal(2, entry.Version));
    }

    private static async Task<IReadOnlyList<DispositionLedgerEntry>> LedgerAsync(
        IPlatformStateStore store,
        string ledgerId)
    {
        const string snapshot = "cafe0000000000000000000000000000000000000000000000000000000000ff";

        DispositionLedgerEntry[] entries =
        [
            .. new[] { "ORD_NO", "ORD_DATE", "ORD_TOTAL" }.Select(property => new DispositionLedgerEntry
            {
                LedgerId = ledgerId,
                EntryId = $"{ledgerId}-{property}",
                TenantId = Tenant,
                ProjectId = "prj-batch",
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
                ProjectId = "prj-batch",
                RunId = $"run-{ledgerId}",
                SourceSnapshotHash = snapshot,
                SourceRoot = "legacy/forms",
                IntermediateContentSha256 = new string('a', 64),
                CreatedUtc = DateTimeOffset.UtcNow,
                CreatedByObjectId = Requester,
                ModuleCount = 1,
                EntryCount = entries.Length,
            },
            entries,
            CancellationToken.None));

        return entries;
    }

    private static Task<IReadOnlyList<DispositionLedgerEntry>> Reread(IPlatformStateStore store, string ledgerId) =>
        store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None);

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

        public Task<SourceEnvironmentProfile?> CreateSourceEnvironmentProfileAsync(
            SourceEnvironmentProfile profile, CancellationToken cancellationToken) =>
            inner.CreateSourceEnvironmentProfileAsync(profile, cancellationToken);

        public Task<SourceEnvironmentProfile?> GetSourceEnvironmentProfileAsync(
            string tenantId, string projectId, string sourceEnvironmentId, int? version, CancellationToken cancellationToken) =>
            inner.GetSourceEnvironmentProfileAsync(tenantId, projectId, sourceEnvironmentId, version, cancellationToken);

        public Task<IReadOnlyList<SourceEnvironmentProfile>> SourceEnvironmentProfilesAsync(
            string tenantId, string projectId, CancellationToken cancellationToken) =>
            inner.SourceEnvironmentProfilesAsync(tenantId, projectId, cancellationToken);

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

        public Task<DispositionLedger?> CreateDispositionLedgerAsync(
            DispositionLedger ledger, IReadOnlyList<DispositionLedgerEntry> entries, CancellationToken cancellationToken) =>
            inner.CreateDispositionLedgerAsync(ledger, entries, cancellationToken);

        public Task<DispositionLedger?> GetDispositionLedgerAsync(
            string tenantId, string ledgerId, CancellationToken cancellationToken) =>
            inner.GetDispositionLedgerAsync(tenantId, ledgerId, cancellationToken);

        public Task<IReadOnlyList<DispositionLedger>> DispositionLedgersAsync(
            string tenantId, string projectId, CancellationToken cancellationToken) =>
            inner.DispositionLedgersAsync(tenantId, projectId, cancellationToken);

        public Task<IReadOnlyList<DispositionLedgerEntry>> DispositionLedgerEntriesAsync(
            string tenantId, string ledgerId, CancellationToken cancellationToken) =>
            inner.DispositionLedgerEntriesAsync(tenantId, ledgerId, cancellationToken);

        public Task<DispositionLedgerEntry?> UpdateDispositionLedgerEntryAsync(
            DispositionLedgerEntry entry, int expectedVersion, CancellationToken cancellationToken) =>
            inner.UpdateDispositionLedgerEntryAsync(entry, expectedVersion, cancellationToken);

        public Task<IReadOnlyList<DispositionLedgerEntry>?> UpdateDispositionLedgerEntriesAsync(
            IReadOnlyList<DispositionLedgerEntryUpdate> updates, CancellationToken cancellationToken) =>
            inner.UpdateDispositionLedgerEntriesAsync(updates, cancellationToken);
    }
}
