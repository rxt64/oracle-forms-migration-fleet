// Copyright (c) Microsoft. All rights reserved.

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// The server-owned trust boundary, exercised through the exact methods the workbench endpoints call.
///
/// These are not HTTP tests. <c>WorkbenchEndpoints</c> is internal and its handlers are lambdas
/// registered on an <c>IEndpointRouteBuilder</c>, so reaching them over the wire would mean hosting the
/// app in a TestServer and bringing ASP.NET test hosting into a suite that is otherwise offline and
/// dependency-free. Instead the endpoints were reduced to two call sites —
/// <see cref="WorkbenchExecution.TryPlanRun"/> and <see cref="WorkbenchExecution.TryPrepareRun"/> — and
/// every assertion below runs against those. What is therefore NOT covered here is the HTTP layer
/// itself: routing, the JSON body read, and the header parsing that builds the actor. Those are thin and
/// visible in <c>WorkbenchEndpoints</c>, but they are not under test.
/// </summary>
public class WorkbenchTrustBoundaryTests : IDisposable
{
    private const string Owner = "user-a";
    private const string Intruder = "user-b";

    private static readonly WorkbenchActor Operator =
        new(Owner, [WorkbenchTrustBoundary.MigrationOperatorRole]);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ofm-trust-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A request in which every claim of authority a browser can make has been made.</summary>
    private static MigrationRunRequest ForgedRequest(
        string engagementId = "ENG-TRUST",
        ExecutionMode mode = ExecutionMode.ProductionCutover,
        DatabaseTarget database = DatabaseTarget.PostgreSql) => new()
        {
            EngagementId = engagementId,
            ApplicationName = "ORDERS",
            RequestedMode = mode,
            Target = new TargetStack { Database = database },
            SourceRoot = ".",
            OutputRoot = "out",
            Evidence =
            [
                new EvidenceItem
                {
                    Id = "EV-1",
                    Kind = EvidenceKind.TestBaseline,
                    Source = "operator says so",
                    Summary = "A regression baseline exists.",
                    IsVerified = true,
                    Signals = [WorkloadSignal.SelfContainedSchema, WorkloadSignal.MissingRegressionBaseline],
                },
                new EvidenceItem
                {
                    Id = "EV-2",
                    Kind = EvidenceKind.DatabaseSchemaExport,
                    Source = "operator says so",
                    Summary = "The schema was exported.",
                    IsVerified = true,
                    Signals = [WorkloadSignal.LargeDatabaseFootprint],
                },
            ],
            PlanApproval = new HumanApproval { Decision = ApprovalDecision.Approved, ApproverId = "forged-plan@contoso.example" },
            ExecutionApproval = new HumanApproval { Decision = ApprovalDecision.Approved, ApproverId = "forged-exec@contoso.example" },
            ProductionApproval = new HumanApproval { Decision = ApprovalDecision.Approved, ApproverId = "forged-prod@contoso.example" },
            Attestations =
            [
                new MigrationAttestation
                {
                    Kind = AttestationKind.SandboxMigrationCompleted,
                    Succeeded = true,
                    AttestedBy = "forged@contoso.example",
                    Summary = "The sandbox migration completed.",
                    Artifacts = [new ArtifactReference("out/sandbox.md", ArtifactKind.ValidationReport, "forged")],
                },
                new MigrationAttestation
                {
                    Kind = AttestationKind.DataReconciliationPassed,
                    Succeeded = true,
                    AttestedBy = "forged@contoso.example",
                    Summary = "Reconciliation passed.",
                    Artifacts = [new ArtifactReference("out/recon.md", ArtifactKind.ReconciliationReport, "forged")],
                },
                new MigrationAttestation
                {
                    Kind = AttestationKind.HumanAcceptanceSigned,
                    Succeeded = true,
                    AttestedBy = "forged@contoso.example",
                    Summary = "Acceptance signed.",
                    Artifacts = [new ArtifactReference("out/accept.md", ArtifactKind.ValidationReport, "forged")],
                },
            ],
        };

    private async Task<(SourceWorkspaceService Service, string WorkspaceId)> SeedAsync(
        string owner = Owner,
        string content = "binary-form",
        bool includeDatabase = true)
    {
        SourceWorkspaceService service = new(_root);

        using MemoryStream archive = new();
        using (ZipArchive zip = new(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (Stream form = zip.CreateEntry("forms/ORDERS.fmb").Open())
            {
                form.Write(Encoding.UTF8.GetBytes(content));
            }

            if (includeDatabase)
            {
                using (Stream package = zip.CreateEntry("db/PKG_ORDERS.pks").Open())
                {
                    package.Write(Encoding.UTF8.GetBytes("CREATE OR REPLACE PACKAGE PKG_ORDERS AS END;"));
                }

                using Stream export = zip.CreateEntry("db/schema.dmp").Open();
                export.Write(Encoding.UTF8.GetBytes("export"));
            }
        }

        archive.Position = 0;

        string? workspaceId = null;
        await foreach (SourceProgress step in service.ExtractAsync(owner, archive, "orders.zip", CancellationToken.None))
        {
            if (step.Level == "done")
            {
                workspaceId = step.Text;
            }
        }

        Assert.NotNull(workspaceId);
        return (service, workspaceId);
    }

    [Fact]
    public async Task Plan_endpoint_refuses_a_forged_verification_flag_and_forged_signals()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        WorkbenchPlanResponse response = Plan(service, workspaceId, ForgedRequest());

        EvidenceItem baseline = Assert.Single(
            PlannedEvidence(service, workspaceId), item => item.Kind == EvidenceKind.TestBaseline);
        Assert.False(baseline.IsVerified);
        Assert.Empty(baseline.Signals);

        // The forged TestBaseline was the only thing standing between this request and generation.
        Assert.Contains(response.Plan.Blockers, blocker => blocker.Contains("TestBaseline", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plan_endpoint_derives_verified_evidence_only_from_the_indexed_source()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        IReadOnlyList<EvidenceItem> evidence = PlannedEvidence(service, workspaceId);

        Assert.All(evidence.Where(item => item.IsVerified), item =>
        {
            Assert.StartsWith("SRC-", item.Id, StringComparison.Ordinal);
            Assert.Equal("server:source-workspace-index", item.Source);
        });

        // The seeded archive holds a .fmb, which the name-only indexer recognises.
        Assert.Contains(evidence, item => item.Kind == EvidenceKind.FormsModuleSource && item.IsVerified);

        // No file name can establish a regression baseline, so the caller's claim of one stays a claim.
        Assert.Contains(evidence, item => item.Kind == EvidenceKind.TestBaseline && !item.IsVerified);
    }

    [Fact]
    public async Task Plan_endpoint_derives_evidence_only_from_the_selected_source_folder()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        MigrationRunRequest formsOnly = ForgedRequest() with { SourceRoot = "forms" };
        WorkbenchPlanResponse response = Plan(service, workspaceId, formsOnly);

        Assert.Contains(response.Plan.Blockers, blocker => blocker.Contains("DatabaseSchemaExport", StringComparison.Ordinal));
        Assert.Contains(response.Plan.Blockers, blocker => blocker.Contains("PlSqlProgramUnit", StringComparison.Ordinal));

        WorkbenchRequestPreparation prepared = WorkbenchTrustBoundary.Prepare(
            formsOnly,
            service.Describe(Owner, workspaceId, formsOnly.SourceRoot));
        Assert.DoesNotContain(prepared.Request.Evidence, item =>
            item.IsVerified && item.Kind is EvidenceKind.DatabaseSchemaExport or EvidenceKind.PlSqlProgramUnit);
    }

    [Fact]
    public async Task Workbench_output_never_becomes_verified_source_evidence()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync(includeDatabase: false);
        using SourceWorkspaceService owned = service;

        string root = service.ResolveRoot(Owner, workspaceId)!;
        string generated = Path.Combine(root, WorkbenchExecution.OutputRoot, "out", "database", "schema.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(generated)!);
        File.WriteAllText(generated, "CREATE TABLE GENERATED_BY_THE_WORKBENCH (ID integer);");

        MigrationRunRequest wholeWorkspace = ForgedRequest() with { SourceRoot = "." };
        WorkbenchRequestPreparation prepared = WorkbenchTrustBoundary.Prepare(
            wholeWorkspace,
            service.Describe(Owner, workspaceId, wholeWorkspace.SourceRoot));

        Assert.DoesNotContain(prepared.Request.Evidence, item =>
            item.IsVerified && item.Kind == EvidenceKind.DatabaseSchemaExport);
    }

    [Fact]
    public async Task Plan_endpoint_rejects_the_workbench_output_as_a_source_folder()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        Assert.False(WorkbenchExecution.TryPlanRun(
            service,
            Operator,
            workspaceId,
            ForgedRequest() with { SourceRoot = WorkbenchExecution.OutputRoot },
            out _,
            out int status,
            out string error));

        Assert.Equal(400, status);
        Assert.Contains("output", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(".fleet-run")]
    [InlineData(".fleet-run/out")]
    public async Task Plan_and_execute_reject_any_source_inside_the_workbench_output(string sourceRoot)
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;
        MigrationRunRequest request = ForgedRequest() with { SourceRoot = sourceRoot };

        Assert.False(WorkbenchExecution.TryPlanRun(
            service, Operator, workspaceId, request, out _, out int planStatus, out _));
        Assert.Equal(400, planStatus);

        WorkbenchExecution.WorkbenchRunPreparationResult execute = await WorkbenchExecution.PrepareRunAsync(
            service, Operator, workspaceId, request, new WorkbenchAuthorizationService());
        Assert.False(execute.Succeeded);
        Assert.Equal(400, execute.Status);
    }

    [Fact]
    public async Task Plan_endpoint_rejects_a_workspace_owned_by_someone_else()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        Assert.False(WorkbenchExecution.TryPlanRun(
            service,
            new WorkbenchActor(Intruder, [WorkbenchTrustBoundary.MigrationOperatorRole]),
            workspaceId,
            ForgedRequest(),
            out WorkbenchPlanResponse? response,
            out int status,
            out string error));

        Assert.Null(response);
        Assert.Equal(404, status);
        Assert.NotEqual(string.Empty, error);
    }

    [Fact]
    public void Plan_endpoint_leaves_every_declaration_unverified_when_no_workspace_is_supplied()
    {
        WorkbenchPlanResponse response = Plan(null, null, ForgedRequest());

        Assert.Equal(ExecutionMode.PlanOnly, response.Plan.AuthorizedMode);
        Assert.NotEmpty(response.Plan.Blockers);
    }

    [Fact]
    public async Task Plan_endpoint_still_authorizes_artifact_generation_from_indexed_source()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        WorkbenchPlanResponse response = Plan(
            service, workspaceId, ForgedRequest(mode: ExecutionMode.GenerateArtifacts));

        Assert.True(
            response.Plan.AuthorizedMode == ExecutionMode.GenerateArtifacts,
            $"Authorized {response.Plan.AuthorizedMode}. Blockers: {string.Join(" | ", response.Plan.Blockers)}");
        Assert.Contains(response.Plan.Phases, phase => phase.Status == PhaseStatus.Planned && phase.Mutation == MutationClass.WorkspaceArtifactWrite);
    }

    [Fact]
    public async Task Plan_endpoint_never_authorizes_a_mutation_on_the_strength_of_forged_approvals()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        WorkbenchPlanResponse response = Plan(service, workspaceId, ForgedRequest());

        Assert.True(response.Plan.AuthorizedMode <= ExecutionMode.GenerateArtifacts);
        Assert.DoesNotContain(
            response.Plan.Phases,
            phase => phase.Status == PhaseStatus.Planned
                && phase.Mutation is MutationClass.SandboxDatabaseWrite or MutationClass.ProductionWrite);
    }

    [Fact]
    public async Task Execute_endpoint_clears_forged_approvals_and_attestations_before_planning()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        WorkbenchExecution.WorkbenchRunPreparationResult result = await WorkbenchExecution.PrepareRunAsync(
            service, Operator, workspaceId, ForgedRequest(), new WorkbenchAuthorizationService());

        Assert.True(result.Succeeded);
        Assert.Equal(200, result.Status);
        MigrationRunRequest prepared = result.Preparation!.Request;

        Assert.Equal(ApprovalDecision.Pending, prepared.PlanApproval.Decision);
        Assert.Equal(ApprovalDecision.Pending, prepared.ExecutionApproval.Decision);
        Assert.Equal(ApprovalDecision.Pending, prepared.ProductionApproval.Decision);
        Assert.Null(prepared.ExecutionApproval.ApproverId);
        Assert.Null(prepared.ProductionApproval.ApproverId);
        Assert.Empty(prepared.Attestations);
        Assert.All(prepared.Evidence, item => Assert.Empty(item.Signals));
    }

    [Fact]
    public async Task Execute_endpoint_refuses_a_workspace_the_caller_does_not_own()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        WorkbenchExecution.WorkbenchRunPreparationResult result = await WorkbenchExecution.PrepareRunAsync(
            service, new WorkbenchActor(Intruder, [WorkbenchTrustBoundary.MigrationOperatorRole]),
            workspaceId, ForgedRequest(), new WorkbenchAuthorizationService());

        Assert.False(result.Succeeded);
        Assert.Equal(404, result.Status);
        Assert.Null(result.Preparation);
    }

    [Fact]
    public async Task Execute_endpoint_supplies_a_fail_closed_authorizer_when_no_grant_exists()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        WorkbenchExecution.WorkbenchRunPreparation preparation = (await WorkbenchExecution.PrepareRunAsync(
            service, Operator, workspaceId, ForgedRequest(), new WorkbenchAuthorizationService())).Preparation!;

        foreach (MutationClass mutation in new[] { MutationClass.SandboxDatabaseWrite, MutationClass.ProductionWrite })
        {
            MutationAuthorizationResult decision = await preparation.MutationAuthorizer.AuthorizeAsync(
                new MutationAuthorizationRequest(MigrationPhase.SandboxDataMigration, mutation, preparation.Request, Owner),
                CancellationToken.None);

            Assert.False(decision.IsAuthorized);
            Assert.NotEqual(string.Empty, decision.Reason);
        }
    }

    [Fact]
    public async Task A_scoped_grant_authorizes_the_run_it_was_issued_for()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        StubAuthorizationStore store = new();
        WorkbenchAuthorizationService authorization = new(store);

        WorkbenchExecution.WorkbenchRunPreparation preparation = (await WorkbenchExecution.PrepareRunAsync(
            service, Operator, workspaceId, ForgedRequest(), authorization)).Preparation!;

        store.Records = [Grant(service, workspaceId, preparation.Request)];

        MutationAuthorizationResult decision = await preparation.MutationAuthorizer.AuthorizeAsync(
            new MutationAuthorizationRequest(
                MigrationPhase.SandboxDataMigration, MutationClass.SandboxDatabaseWrite, preparation.Request, Owner),
            CancellationToken.None);

        Assert.True(decision.IsAuthorized);
    }

    /// <summary>
    /// Spec 004 replaced the earlier behaviour. A persisted grant that matches the run in every binding
    /// now materializes the internal execution approval, and it names the actor who approved it in the
    /// store rather than anything the caller sent. A forged approval in the body is still discarded first,
    /// so the only route to an approved gate is a record the server itself holds.
    /// </summary>
    [Fact]
    public async Task A_matching_store_record_materializes_the_internal_execution_approval()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        StubAuthorizationStore store = new();
        WorkbenchAuthorizationService authorization = new(store);

        // The first pass establishes the bindings the grant has to match.
        WorkbenchExecution.WorkbenchRunPreparation first = (await WorkbenchExecution.PrepareRunAsync(
            service, Operator, workspaceId, ForgedRequest(), authorization)).Preparation!;

        Assert.Equal(ApprovalDecision.Pending, first.Request.ExecutionApproval.Decision);

        store.Records =
        [
            Grant(service, workspaceId, first.Request) with { ApprovedByObjectId = "approver-object-id" },
        ];

        WorkbenchExecution.WorkbenchRunPreparation second = (await WorkbenchExecution.PrepareRunAsync(
            service, Operator, workspaceId, ForgedRequest(), authorization)).Preparation!;

        Assert.Equal(ApprovalDecision.Approved, second.Request.ExecutionApproval.Decision);
        Assert.Equal("approver-object-id", second.Request.ExecutionApproval.ApproverId);

        // The forged approver the browser sent never appears, and production stays refused.
        Assert.DoesNotContain("forged", second.Request.ExecutionApproval.ApproverId!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ApprovalDecision.Pending, second.Request.ProductionApproval.Decision);
    }

    [Fact]
    public async Task A_persisted_approval_for_the_raw_browser_request_materializes_after_execution_normalization()
    {
        WorkbenchActor requester = WorkbenchActor.ForTenant(
            WorkbenchAuthenticationOptions.DevelopmentTenantId,
            "requester",
            []);
        WorkbenchActor approver = WorkbenchActor.ForTenant(
            WorkbenchAuthenticationOptions.DevelopmentTenantId,
            "approver",
            []);
        ConfiguredSandboxTargetBinding sandbox = new(
            "pg-sandbox.postgres.database.azure.com",
            "ofm_sandbox",
            "id-ofmfleet-web-dev",
            CanWrite: false);

        FilePlatformStateStore store = new(Path.Combine(_root, "platform-state.json"));
        await store.InitializeAsync(CancellationToken.None);
        PlatformAccessService platform = new(store, sandbox);
        PlatformProject project = (await platform.CreateProjectAsync(
            requester, "Orders migration", CancellationToken.None)).Value!;
        await platform.AddMemberAsync(
            requester,
            project.ProjectId,
            approver.ObjectId,
            [WorkbenchRoles.MigrationOperator, WorkbenchRoles.SandboxApprover],
            CancellationToken.None);

        PlatformTargetProfile profile = (await platform.EnsureConfiguredTargetProfileAsync(
            requester,
            project.ProjectId,
            new PlatformTargetProfileEnvironment
            {
                AzureTenantId = "11111111-1111-1111-1111-111111111111",
                SubscriptionId = "22222222-2222-2222-2222-222222222222",
                ResourceGroup = "rg-ofm-dev",
                ResourceId = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-ofm-dev/providers/Microsoft.DBforPostgreSQL/flexibleServers/pg-sandbox",
                Region = "eastus2",
                SchemaName = "public",
                EnvironmentName = "Development",
            },
            CancellationToken.None)).Value!;

        string workspaceOwner = PlatformIdentity.WorkspaceOwner(requester, project.ProjectId);
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync(owner: workspaceOwner);
        using SourceWorkspaceService owned = service;
        MigrationRunRequest raw = ForgedRequest(mode: ExecutionMode.SandboxMigration);

        Assert.True(WorkbenchExecution.TryPrepareTrusted(
            service,
            workspaceOwner,
            workspaceId,
            raw,
            out WorkbenchExecution.WorkbenchTrustedPreparation? trusted,
            out _,
            out _));

        PlatformApproval requested = (await platform.RequestAsync(
            requester,
            new PlatformApprovalRequestInput(
                project.ProjectId,
                profile.TargetProfileId,
                WorkbenchMutationScope.SandboxDatabaseWrite,
                trusted!.Trusted.Request.EngagementId,
                trusted.Trusted.SourceSnapshotHash,
                trusted.Trusted.PlanInputHash,
                TimeSpan.FromHours(1),
                null,
                trusted.Trusted.Request.Target),
            CancellationToken.None)).Value!;
        await platform.DecideAsync(
            approver, requested.ApprovalId, approve: true, requested.Version, null, CancellationToken.None);

        WorkbenchExecution.WorkbenchRunPreparationResult execution = await WorkbenchExecution.PrepareRunAsync(
            service,
            requester,
            workspaceId,
            raw,
            new WorkbenchAuthorizationService(new PlatformAuthorizationStore(store, sandbox)),
            new WorkbenchExecution.WorkbenchRunBinding(
                project.ProjectId,
                profile.TargetProfileId,
                profile.Version,
                profile.CanonicalHash,
                workspaceOwner,
                WorkbenchTargetProfileStack.From(profile)));

        Assert.True(execution.Succeeded, execution.Error);
        Assert.Equal(".fleet-run/out", execution.Preparation!.Request.OutputRoot);
        Assert.Equal(ApprovalDecision.Approved, execution.Preparation.Request.ExecutionApproval.Decision);
        Assert.Equal(approver.ObjectId, execution.Preparation.Request.ExecutionApproval.ApproverId);

        // A request naming a target the profile does not is refused outright now, rather than being
        // prepared with a pending approval and left to the plan-input hash to catch.
        WorkbenchExecution.WorkbenchRunPreparationResult changedTarget = await WorkbenchExecution.PrepareRunAsync(
            service,
            requester,
            workspaceId,
            raw with { Target = raw.Target with { Database = DatabaseTarget.AzureSqlDatabase } },
            new WorkbenchAuthorizationService(new PlatformAuthorizationStore(store, sandbox)),
            new WorkbenchExecution.WorkbenchRunBinding(
                project.ProjectId,
                profile.TargetProfileId,
                profile.Version,
                profile.CanonicalHash,
                workspaceOwner,
                WorkbenchTargetProfileStack.From(profile)));

        Assert.False(changedTarget.Succeeded);
        Assert.Equal(409, changedTarget.Status);
        Assert.Null(changedTarget.Preparation);
    }

    /// <summary>
    /// The plan-input hash only catches drift between an approval and the run it was issued for. An
    /// approval requested for a stack the deployment does not generate hashes that stack consistently,
    /// so it matched, and the run executed against a destination identity nobody approved. The approval
    /// route now refuses to record one, and the execute route refuses the run even while holding a live
    /// grant that is correctly bound in every other respect.
    /// </summary>
    [Fact]
    public async Task A_back_end_the_target_profile_does_not_name_is_refused_at_approval_and_again_at_execution()
    {
        WorkbenchActor requester = WorkbenchActor.ForTenant(
            WorkbenchAuthenticationOptions.DevelopmentTenantId, "requester", []);
        WorkbenchActor approver = WorkbenchActor.ForTenant(
            WorkbenchAuthenticationOptions.DevelopmentTenantId, "approver", []);
        ConfiguredSandboxTargetBinding sandbox = new(
            "pg-sandbox.postgres.database.azure.com", "ofm_sandbox", "id-ofmfleet-web-dev", CanWrite: false);

        FilePlatformStateStore store = new(Path.Combine(_root, "platform-state.json"));
        await store.InitializeAsync(CancellationToken.None);
        PlatformAccessService platform = new(store, sandbox);
        PlatformProject project = (await platform.CreateProjectAsync(
            requester, "Orders migration", CancellationToken.None)).Value!;
        await platform.AddMemberAsync(
            requester,
            project.ProjectId,
            approver.ObjectId,
            [WorkbenchRoles.MigrationOperator, WorkbenchRoles.SandboxApprover],
            CancellationToken.None);

        PlatformTargetProfile profile = (await platform.EnsureConfiguredTargetProfileAsync(
            requester, project.ProjectId, DevelopmentEnvironment, CancellationToken.None)).Value!;
        Assert.Equal(nameof(BackEndStack.JavaSpringBoot), profile.StackBackEnd);

        string workspaceOwner = PlatformIdentity.WorkspaceOwner(requester, project.ProjectId);
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync(owner: workspaceOwner);
        using SourceWorkspaceService owned = service;

        MigrationRunRequest dotnet = ForgedRequest(mode: ExecutionMode.SandboxMigration) with
        {
            Target = new TargetStack { Database = DatabaseTarget.PostgreSql, BackEnd = BackEndStack.AspNetCore },
        };

        Assert.True(WorkbenchExecution.TryPrepareTrusted(
            service,
            workspaceOwner,
            workspaceId,
            dotnet,
            out WorkbenchExecution.WorkbenchTrustedPreparation? trusted,
            out _,
            out _));

        PlatformResult<PlatformApproval> refused = await platform.RequestAsync(
            requester,
            new PlatformApprovalRequestInput(
                project.ProjectId,
                profile.TargetProfileId,
                WorkbenchMutationScope.SandboxDatabaseWrite,
                trusted!.Trusted.Request.EngagementId,
                trusted.Trusted.SourceSnapshotHash,
                trusted.Trusted.PlanInputHash,
                TimeSpan.FromHours(1),
                null,
                trusted.Trusted.Request.Target),
            CancellationToken.None);

        Assert.False(refused.Succeeded);
        Assert.Equal(409, refused.Status);
        Assert.Contains(nameof(BackEndStack.JavaSpringBoot), refused.Error, StringComparison.Ordinal);
        Assert.Empty(await store.ApprovalsForProjectAsync(
            requester.TenantId, project.ProjectId, CancellationToken.None));

        // Give the run the approval it would have held before this fix: bound to this exact source,
        // plan input, and profile version, decided by a real approver, and unexpired.
        await store.BindSandboxProjectAsync(requester.TenantId, project.ProjectId, CancellationToken.None);
        PlatformApproval planted = await store.CreateApprovalAsync(
            new PlatformApproval
            {
                ApprovalId = "apr-preexisting",
                ProjectId = project.ProjectId,
                TenantId = requester.TenantId,
                RequestedByObjectId = requester.ObjectId,
                RequestedUtc = DateTimeOffset.UtcNow,
                State = PlatformApprovalState.Requested,
                Scope = WorkbenchMutationScope.SandboxDatabaseWrite,
                RequiredRole = WorkbenchRoles.MigrationOperator,
                EngagementId = trusted.Trusted.Request.EngagementId,
                SourceSnapshotHash = trusted.Trusted.SourceSnapshotHash,
                PlanInputHash = WorkbenchTrustBoundary.PlanInputHash(trusted.Trusted.Request),
                TargetProfileId = profile.TargetProfileId,
                TargetProfileVersion = profile.Version,
                TargetProfileHash = profile.CanonicalHash,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
            },
            CancellationToken.None);

        Assert.True((await platform.DecideAsync(
            approver, planted.ApprovalId, approve: true, planted.Version, null, CancellationToken.None)).Succeeded);

        // The grant really is live, so the refusal below is the profile check and nothing else.
        Assert.NotEmpty(await new PlatformAuthorizationStore(store, sandbox)
            .ForOwnerAsync(requester.OwnerId, CancellationToken.None));

        WorkbenchExecution.WorkbenchRunPreparationResult execution = await WorkbenchExecution.PrepareRunAsync(
            service,
            requester,
            workspaceId,
            dotnet,
            new WorkbenchAuthorizationService(new PlatformAuthorizationStore(store, sandbox)),
            new WorkbenchExecution.WorkbenchRunBinding(
                project.ProjectId,
                profile.TargetProfileId,
                profile.Version,
                profile.CanonicalHash,
                workspaceOwner,
                WorkbenchTargetProfileStack.From(profile)));

        Assert.False(execution.Succeeded);
        Assert.Equal(409, execution.Status);
        Assert.Null(execution.Preparation);
        Assert.Contains(nameof(BackEndStack.JavaSpringBoot), execution.Error, StringComparison.Ordinal);

        // The same run against the stack the profile does name still prepares, so the check refuses the
        // mismatch and not the feature.
        MigrationRunRequest java = dotnet with { Target = dotnet.Target with { BackEnd = BackEndStack.JavaSpringBoot } };
        Assert.True((await WorkbenchExecution.PrepareRunAsync(
            service,
            requester,
            workspaceId,
            java,
            new WorkbenchAuthorizationService(new PlatformAuthorizationStore(store, sandbox)),
            new WorkbenchExecution.WorkbenchRunBinding(
                project.ProjectId,
                profile.TargetProfileId,
                profile.Version,
                profile.CanonicalHash,
                workspaceOwner,
                WorkbenchTargetProfileStack.From(profile)))).Succeeded);
    }

    /// <summary>
    /// An undefined stack cannot match a profile name, so it is refused for the same reason a wrong one
    /// is. This is the backstop behind the serializer refusing integers at the boundary.
    /// </summary>
    [Fact]
    public void An_undefined_stack_value_matches_no_profile_and_is_refused()
    {
        WorkbenchTargetProfileStack profile = new(
            nameof(DatabaseTarget.PostgreSql), nameof(FrontEndStack.React), nameof(BackEndStack.JavaSpringBoot));

        Assert.False(WorkbenchExecution.TryMatchTargetProfile(null, profile, out string missing));
        Assert.NotEqual(string.Empty, missing);

        Assert.False(WorkbenchExecution.TryMatchTargetProfile(
            new TargetStack { Database = DatabaseTarget.PostgreSql, BackEnd = (BackEndStack)999 },
            profile,
            out string undefinedBackEnd));
        Assert.Contains(nameof(BackEndStack.JavaSpringBoot), undefinedBackEnd, StringComparison.Ordinal);

        Assert.False(WorkbenchExecution.TryMatchTargetProfile(
            new TargetStack { Database = (DatabaseTarget)77 },
            profile,
            out string undefinedDatabase));
        Assert.Contains(nameof(DatabaseTarget.PostgreSql), undefinedDatabase, StringComparison.Ordinal);

        Assert.True(WorkbenchExecution.TryMatchTargetProfile(
            new TargetStack { Database = DatabaseTarget.PostgreSql },
            profile,
            out string matched));
        Assert.Equal(string.Empty, matched);
    }

    private static string RequestJson(
        string database = "\"PostgreSql\"",
        string backEnd = "\"JavaSpringBoot\"",
        string mode = "\"GenerateArtifacts\"") =>
        $$"""
        {
          "engagementId": "ENG-ENUM",
          "applicationName": "ORDERS",
          "requestedMode": {{mode}},
          "target": { "database": {{database}}, "frontEnd": "React", "backEnd": {{backEnd}} },
          "sourceRoot": ".",
          "outputRoot": "out"
        }
        """;

    /// <summary>
    /// Both HTTP request readers accept enum names only. An ordinal would let a caller name a stack by
    /// number and land on whichever value sits there, or on no value at all.
    /// </summary>
    [Theory]
    [InlineData("1", "\"JavaSpringBoot\"", "\"GenerateArtifacts\"")]
    [InlineData("\"PostgreSql\"", "1", "\"GenerateArtifacts\"")]
    [InlineData("\"PostgreSql\"", "999", "\"GenerateArtifacts\"")]
    [InlineData("\"PostgreSql\"", "\"NodeExpress\"", "\"GenerateArtifacts\"")]
    [InlineData("\"PostgreSql\"", "\"JavaSpringBoot\"", "2")]
    [InlineData("\"AzureCosmosDb\"", "\"JavaSpringBoot\"", "\"GenerateArtifacts\"")]
    public void Both_request_boundaries_refuse_an_enum_value_that_is_not_a_name_this_build_defines(
        string database, string backEnd, string mode)
    {
        string json = RequestJson(database, backEnd, mode);

        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<MigrationRunRequest>(json, WorkbenchEndpoints.RequestOptions));
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<MigrationRunRequest>(json, WorkbenchPlatformEndpoints.RequestOptions));
    }

    [Fact]
    public void Both_request_boundaries_still_accept_the_enum_names_the_console_posts()
    {
        foreach (JsonSerializerOptions options in
            new[] { WorkbenchEndpoints.RequestOptions, WorkbenchPlatformEndpoints.RequestOptions })
        {
            MigrationRunRequest parsed = JsonSerializer.Deserialize<MigrationRunRequest>(RequestJson(), options)!;

            Assert.Equal(DatabaseTarget.PostgreSql, parsed.Target.Database);
            Assert.Equal(FrontEndStack.React, parsed.Target.FrontEnd);
            Assert.Equal(BackEndStack.JavaSpringBoot, parsed.Target.BackEnd);
            Assert.Equal(ExecutionMode.GenerateArtifacts, parsed.RequestedMode);
        }
    }

    private static PlatformTargetProfileEnvironment DevelopmentEnvironment => new()
    {
        AzureTenantId = "11111111-1111-1111-1111-111111111111",
        SubscriptionId = "22222222-2222-2222-2222-222222222222",
        ResourceGroup = "rg-ofm-dev",
        ResourceId = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-ofm-dev/providers/Microsoft.DBforPostgreSQL/flexibleServers/pg-sandbox",
        Region = "eastus2",
        SchemaName = "public",
        EnvironmentName = "Development",
    };

    [Fact]
    public async Task A_valid_grant_is_not_hidden_by_an_older_expired_matching_record()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        StubAuthorizationStore store = new();
        WorkbenchAuthorizationService authorization = new(store);
        WorkbenchExecution.WorkbenchRunPreparation preparation = (await WorkbenchExecution.PrepareRunAsync(
            service, Operator, workspaceId, ForgedRequest(), authorization)).Preparation!;

        WorkbenchAuthorizationRecord valid = Grant(service, workspaceId, preparation.Request);
        store.Records = [valid with { AuthorizationId = "AUTH-EXPIRED", ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }, valid];

        Assert.True((await preparation.MutationAuthorizer.AuthorizeAsync(new MutationAuthorizationRequest(
            MigrationPhase.SandboxDataMigration,
            MutationClass.SandboxDatabaseWrite,
            preparation.Request,
            Owner), CancellationToken.None)).IsAuthorized);
    }

    [Fact]
    public async Task A_grant_is_rechecked_before_every_mutation_so_a_later_expiry_denies()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        StubAuthorizationStore store = new();
        WorkbenchAuthorizationService authorization = new(store);

        WorkbenchExecution.WorkbenchRunPreparation preparation = (await WorkbenchExecution.PrepareRunAsync(
            service, Operator, workspaceId, ForgedRequest(), authorization)).Preparation!;

        WorkbenchAuthorizationRecord grant = Grant(service, workspaceId, preparation.Request);
        store.Records = [grant];

        DateTimeOffset now = grant.ExpiresUtc.AddMinutes(-5);
        WorkbenchMutationAuthorizer authorizer = new(
            authorization,
            Operator,
            service.Describe(Owner, workspaceId)!.SnapshotHash,
            WorkbenchTrustBoundary.PlanInputHash(preparation.Request),
            WorkbenchTrustBoundary.TargetHash(preparation.Request.Target),
            () => now);

        MutationAuthorizationRequest attempt = new(
            MigrationPhase.SandboxDataMigration, MutationClass.SandboxDatabaseWrite, preparation.Request, Owner);

        Assert.True((await authorizer.AuthorizeAsync(attempt, CancellationToken.None)).IsAuthorized);

        // Time moves on between the first mutating phase and the second. The run has not changed; the
        // grant has, and the second attempt must not ride on the first answer.
        now = grant.ExpiresUtc.AddSeconds(1);
        MutationAuthorizationResult second = await authorizer.AuthorizeAsync(attempt, CancellationToken.None);

        Assert.False(second.IsAuthorized);
        Assert.Contains("expired", second.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("engagement")]
    [InlineData("scope")]
    [InlineData("source")]
    [InlineData("input")]
    [InlineData("target")]
    public async Task A_grant_that_does_not_match_the_run_in_every_respect_denies(string drift)
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        StubAuthorizationStore store = new();
        WorkbenchAuthorizationService authorization = new(store);

        WorkbenchExecution.WorkbenchRunPreparation preparation = (await WorkbenchExecution.PrepareRunAsync(
            service, Operator, workspaceId, ForgedRequest(), authorization)).Preparation!;

        WorkbenchAuthorizationRecord grant = Grant(service, workspaceId, preparation.Request);

        switch (drift)
        {
            case "owner":
                grant = grant with { OwnerId = Intruder };
                break;
            case "engagement":
                grant = grant with { EngagementId = "ENG-OTHER" };
                break;
            case "scope":
                grant = grant with { Scope = WorkbenchMutationScope.ProductionWrite };
                break;
            case "source":
                grant = grant with { SourceSnapshotHash = new string('0', grant.SourceSnapshotHash.Length) };
                break;
            case "input":
                grant = grant with { PlanInputHash = new string('0', grant.PlanInputHash.Length) };
                break;
            case "target":
                grant = grant with { TargetHash = new string('0', grant.TargetHash.Length) };
                break;
        }

        store.Records = [grant];

        WorkbenchMutationAuthorizer authorizer = new(
            authorization,
            Operator,
            service.Describe(Owner, workspaceId)!.SnapshotHash,
            WorkbenchTrustBoundary.PlanInputHash(preparation.Request),
            WorkbenchTrustBoundary.TargetHash(preparation.Request.Target));

        MutationAuthorizationResult decision = await authorizer.AuthorizeAsync(new MutationAuthorizationRequest(
            MigrationPhase.SandboxDataMigration, MutationClass.SandboxDatabaseWrite, preparation.Request, Owner),
            CancellationToken.None);

        Assert.False(decision.IsAuthorized);
    }

    [Fact]
    public async Task A_changed_source_copy_produces_a_different_snapshot_hash()
    {
        (SourceWorkspaceService first, string firstId) = await SeedAsync(content: "binary-form");
        using SourceWorkspaceService ownedFirst = first;

        (SourceWorkspaceService second, string secondId) = await SeedAsync(content: "binary-form-edited");
        using SourceWorkspaceService ownedSecond = second;

        (SourceWorkspaceService third, string thirdId) = await SeedAsync(content: "binary-form");
        using SourceWorkspaceService ownedThird = third;

        string a = first.Describe(Owner, firstId)!.SnapshotHash;
        string b = second.Describe(Owner, secondId)!.SnapshotHash;
        string c = third.Describe(Owner, thirdId)!.SnapshotHash;

        Assert.NotEqual(a, b);
        Assert.Equal(a, c);

        // The digest identifies the bytes without carrying them: it is fixed-length hex, not content.
        Assert.Equal(64, a.Length);
        Assert.All(a, character => Assert.Contains(character, "0123456789abcdef"));
    }

    [Fact]
    public void A_changed_plan_input_or_target_produces_a_different_binding()
    {
        MigrationRunRequest request = ForgedRequest();

        Assert.NotEqual(
            WorkbenchTrustBoundary.PlanInputHash(request),
            WorkbenchTrustBoundary.PlanInputHash(request with { SourceRoot = "other" }));

        Assert.NotEqual(
            WorkbenchTrustBoundary.PlanInputHash(request),
            WorkbenchTrustBoundary.PlanInputHash(request with
            {
                Target = request.Target with { Database = DatabaseTarget.AzureSqlDatabase },
            }));
        Assert.NotEqual(
            WorkbenchTrustBoundary.PlanInputHash(request),
            WorkbenchTrustBoundary.PlanInputHash(request with
            {
                Target = request.Target with { FrontEnd = (FrontEndStack)999 },
            }));
        Assert.NotEqual(
            WorkbenchTrustBoundary.PlanInputHash(request),
            WorkbenchTrustBoundary.PlanInputHash(request with
            {
                Target = request.Target with { BackEnd = (BackEndStack)999 },
            }));

        Assert.NotEqual(
            WorkbenchTrustBoundary.TargetHash(request.Target),
            WorkbenchTrustBoundary.TargetHash(new TargetStack { Database = DatabaseTarget.AzureSqlDatabase }));

        // Evidence order is not a difference; evidence content is.
        Assert.Equal(
            WorkbenchTrustBoundary.PlanInputHash(request),
            WorkbenchTrustBoundary.PlanInputHash(request with { Evidence = [.. request.Evidence.Reverse()] }));
    }

    /// <summary>
    /// A grant issued over no recorded decisions does not cover a run that generates under some, and a
    /// grant issued over one ledger does not cover a run that moved to another. Both have to be a
    /// different binding, or an approval taken before a ledger existed would still authorize generating
    /// under it.
    /// </summary>
    [Fact]
    public void Naming_or_changing_a_disposition_ledger_produces_a_different_binding()
    {
        MigrationRunRequest request = ForgedRequest();

        Assert.NotEqual(
            WorkbenchTrustBoundary.PlanInputHash(request),
            WorkbenchTrustBoundary.PlanInputHash(request with { DispositionLedgerId = "dled-0001" }));

        Assert.NotEqual(
            WorkbenchTrustBoundary.PlanInputHash(request with { DispositionLedgerId = "dled-0001" }),
            WorkbenchTrustBoundary.PlanInputHash(request with { DispositionLedgerId = "dled-0002" }));

        // Absent and blank are the same statement: this run names no ledger.
        Assert.Equal(
            WorkbenchTrustBoundary.PlanInputHash(request),
            WorkbenchTrustBoundary.PlanInputHash(request with { DispositionLedgerId = string.Empty }));
    }

    /// <summary>
    /// The recheck is what makes this a boundary rather than a form field: a run the planner authorized,
    /// with a real approval and complete evidence, still stops at the adapter that would write outside
    /// the workspace when the authorizer refuses.
    /// </summary>
    [Fact]
    public async Task The_executor_refuses_an_authorized_mutating_phase_when_the_authorizer_denies()
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/001_schema.sql", OracleSamples.Schema);
        workspace.WriteFile("legacy/forms/db/002_data.sql", "INSERT INTO ORDERS (ID) VALUES (1);");

        MigrationRunRequest request = new()
        {
            EngagementId = "ENG-RECHECK",
            ApplicationName = "ORDERS",
            RequestedMode = ExecutionMode.SandboxMigration,
            Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
            SourceRoot = "legacy/forms",
            OutputRoot = "out/orders",
            Evidence = Requests.CompleteEvidence(),
            ExecutionApproval = Requests.Approved("release-manager@contoso.com"),
        };

        MigrationRunPlan plan = MigrationRunPlanner.Plan(request);
        Assert.Contains(
            plan.Phases,
            phase => phase.Phase == MigrationPhase.SandboxDataMigration
                && phase.Status == PhaseStatus.Planned
                && phase.Mutation == MutationClass.SandboxDatabaseWrite);

        MigrationExecutionResult denied = await new MigrationExecutor(
                workspace.Root,
                [new Fleet.Execution.Adapters.SandboxDataMigrationAdapter(null, null)],
                new DenyingAuthorizer())
            .ExecuteAsync(request, Owner);

        PhaseOutcome outcome = denied.Phases.Single(phase => phase.Phase == MigrationPhase.SandboxDataMigration);
        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Contains("not authorized", outcome.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(outcome.Artifacts);
        Assert.Empty(denied.Attestations);
    }

    /// <summary>
    /// The optional authorizer is opt-in. In-process callers that already trust their own inputs keep the
    /// deterministic contract they have today, so adding the parameter changed no existing behaviour.
    /// </summary>
    [Fact]
    public async Task An_executor_without_an_authorizer_keeps_the_planner_as_the_only_gate()
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/001_schema.sql", OracleSamples.Schema);
        workspace.WriteFile("legacy/forms/db/002_data.sql", "INSERT INTO ORDERS (ID) VALUES (1);");

        MigrationRunRequest request = new()
        {
            EngagementId = "ENG-RECHECK",
            ApplicationName = "ORDERS",
            RequestedMode = ExecutionMode.SandboxMigration,
            Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
            SourceRoot = "legacy/forms",
            OutputRoot = "out/orders",
            Evidence = Requests.CompleteEvidence(),
            ExecutionApproval = Requests.Approved("release-manager@contoso.com"),
        };

        MigrationExecutionResult allowed = await new MigrationExecutor(
                workspace.Root,
                [new Fleet.Execution.Adapters.SandboxDataMigrationAdapter(null, null)])
            .ExecuteAsync(request, Owner);

        PhaseOutcome outcome = allowed.Phases.Single(phase => phase.Phase == MigrationPhase.SandboxDataMigration);
        Assert.NotEqual(PhaseExecutionState.SkippedByPlanner, outcome.State);
        Assert.DoesNotContain("not authorized", outcome.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<EvidenceItem> PlannedEvidence(SourceWorkspaceService service, string workspaceId) =>
        WorkbenchTrustBoundary.Prepare(ForgedRequest(), service.Describe(Owner, workspaceId)).Request.Evidence;

    private static WorkbenchAuthorizationRecord Grant(
        SourceWorkspaceService service,
        string workspaceId,
        MigrationRunRequest prepared) => new()
        {
            AuthorizationId = "AUTH-1",
            OwnerId = Owner,
            RequiredRole = WorkbenchTrustBoundary.MigrationOperatorRole,
            EngagementId = prepared.EngagementId,
            SourceSnapshotHash = service.Describe(Owner, workspaceId)!.SnapshotHash,
            PlanInputHash = WorkbenchTrustBoundary.PlanInputHash(prepared),
            TargetHash = WorkbenchTrustBoundary.TargetHash(prepared.Target),
            Scope = WorkbenchMutationScope.SandboxDatabaseWrite,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
        };

    private sealed class StubAuthorizationStore : IWorkbenchAuthorizationStore
    {
        public IReadOnlyList<WorkbenchAuthorizationRecord> Records { get; set; } = [];

        public Task<IReadOnlyList<WorkbenchAuthorizationRecord>> ForOwnerAsync(string ownerId, CancellationToken cancellationToken) =>
            Task.FromResult(Records);
    }

    private sealed class DenyingAuthorizer : IPhaseMutationAuthorizer
    {
        public Task<MutationAuthorizationResult> AuthorizeAsync(
            MutationAuthorizationRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(MutationAuthorizationResult.Deny("No authorization exists for this run."));
    }

    private static WorkbenchPlanResponse Plan(
        SourceWorkspaceService? service,
        string? workspaceId,
        MigrationRunRequest request)
    {
        Assert.True(WorkbenchExecution.TryPlanRun(
            service,
            Operator,
            workspaceId,
            request,
            out WorkbenchPlanResponse? response,
            out int status,
            out string error),
            $"Status {status}: {error}");

        return Assert.IsType<WorkbenchPlanResponse>(response);
    }
}
