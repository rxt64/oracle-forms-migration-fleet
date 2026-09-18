// Copyright (c) Microsoft. All rights reserved.

using System.IO.Compression;
using System.Text;
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

        Assert.False(WorkbenchExecution.TryPrepareRun(
            service, Operator, workspaceId, request, new WorkbenchAuthorizationService(),
            out _, out int executeStatus, out _));
        Assert.Equal(400, executeStatus);
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

        Assert.True(WorkbenchExecution.TryPrepareRun(
            service, Operator, workspaceId, ForgedRequest(), new WorkbenchAuthorizationService(),
            out WorkbenchExecution.WorkbenchRunPreparation? preparation, out int status, out _));

        Assert.Equal(200, status);
        MigrationRunRequest prepared = preparation!.Request;

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

        Assert.False(WorkbenchExecution.TryPrepareRun(
            service, new WorkbenchActor(Intruder, [WorkbenchTrustBoundary.MigrationOperatorRole]),
            workspaceId, ForgedRequest(), new WorkbenchAuthorizationService(),
            out WorkbenchExecution.WorkbenchRunPreparation? preparation, out int status, out _));

        Assert.Equal(404, status);
        Assert.Null(preparation);
    }

    [Fact]
    public async Task Execute_endpoint_supplies_a_fail_closed_authorizer_when_no_grant_exists()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        Assert.True(WorkbenchExecution.TryPrepareRun(
            service, Operator, workspaceId, ForgedRequest(), new WorkbenchAuthorizationService(),
            out WorkbenchExecution.WorkbenchRunPreparation? preparation, out _, out _));

        foreach (MutationClass mutation in new[] { MutationClass.SandboxDatabaseWrite, MutationClass.ProductionWrite })
        {
            MutationAuthorizationResult decision = preparation!.MutationAuthorizer.Authorize(
                new MutationAuthorizationRequest(MigrationPhase.SandboxDataMigration, mutation, preparation.Request, Owner));

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

        Assert.True(WorkbenchExecution.TryPrepareRun(
            service, Operator, workspaceId, ForgedRequest(), authorization,
            out WorkbenchExecution.WorkbenchRunPreparation? preparation, out _, out _));

        store.Records = [Grant(service, workspaceId, preparation!.Request)];

        MutationAuthorizationResult decision = preparation.MutationAuthorizer.Authorize(
            new MutationAuthorizationRequest(
                MigrationPhase.SandboxDataMigration, MutationClass.SandboxDatabaseWrite, preparation.Request, Owner));

        Assert.True(decision.IsAuthorized);
    }

    [Fact]
    public async Task A_matching_store_record_does_not_materialize_a_planner_approval_in_this_deployment()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        StubAuthorizationStore store = new();
        WorkbenchAuthorizationService authorization = new(store);
        Assert.True(WorkbenchExecution.TryPrepareRun(
            service, Operator, workspaceId, ForgedRequest(), authorization,
            out WorkbenchExecution.WorkbenchRunPreparation? preparation, out _, out _));

        store.Records = [Grant(service, workspaceId, preparation!.Request)];
        MigrationRunPlan plan = MigrationRunPlanner.Plan(preparation.Request);

        Assert.Equal(ApprovalDecision.Pending, preparation.Request.ExecutionApproval.Decision);
        Assert.DoesNotContain(plan.Phases, phase =>
            phase.Status == PhaseStatus.Planned &&
            phase.Mutation is MutationClass.SandboxDatabaseWrite or MutationClass.ProductionWrite);
    }

    [Fact]
    public async Task A_valid_grant_is_not_hidden_by_an_older_expired_matching_record()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        StubAuthorizationStore store = new();
        WorkbenchAuthorizationService authorization = new(store);
        Assert.True(WorkbenchExecution.TryPrepareRun(
            service, Operator, workspaceId, ForgedRequest(), authorization,
            out WorkbenchExecution.WorkbenchRunPreparation? preparation, out _, out _));

        WorkbenchAuthorizationRecord valid = Grant(service, workspaceId, preparation!.Request);
        store.Records = [valid with { AuthorizationId = "AUTH-EXPIRED", ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }, valid];

        Assert.True(preparation.MutationAuthorizer.Authorize(new MutationAuthorizationRequest(
            MigrationPhase.SandboxDataMigration,
            MutationClass.SandboxDatabaseWrite,
            preparation.Request,
            Owner)).IsAuthorized);
    }

    [Fact]
    public async Task A_grant_is_rechecked_before_every_mutation_so_a_later_expiry_denies()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        StubAuthorizationStore store = new();
        WorkbenchAuthorizationService authorization = new(store);

        Assert.True(WorkbenchExecution.TryPrepareRun(
            service, Operator, workspaceId, ForgedRequest(), authorization,
            out WorkbenchExecution.WorkbenchRunPreparation? preparation, out _, out _));

        WorkbenchAuthorizationRecord grant = Grant(service, workspaceId, preparation!.Request);
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

        Assert.True(authorizer.Authorize(attempt).IsAuthorized);

        // Time moves on between the first mutating phase and the second. The run has not changed; the
        // grant has, and the second attempt must not ride on the first answer.
        now = grant.ExpiresUtc.AddSeconds(1);
        MutationAuthorizationResult second = authorizer.Authorize(attempt);

        Assert.False(second.IsAuthorized);
        Assert.Contains("expired", second.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("engagement")]
    [InlineData("role")]
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

        Assert.True(WorkbenchExecution.TryPrepareRun(
            service, Operator, workspaceId, ForgedRequest(), authorization,
            out WorkbenchExecution.WorkbenchRunPreparation? preparation, out _, out _));

        WorkbenchAuthorizationRecord grant = Grant(service, workspaceId, preparation!.Request);
        WorkbenchActor actor = Operator;

        switch (drift)
        {
            case "owner":
                grant = grant with { OwnerId = Intruder };
                break;
            case "engagement":
                grant = grant with { EngagementId = "ENG-OTHER" };
                break;
            case "role":
                actor = new WorkbenchActor(Owner, ["Reader"]);
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
            actor,
            service.Describe(Owner, workspaceId)!.SnapshotHash,
            WorkbenchTrustBoundary.PlanInputHash(preparation.Request),
            WorkbenchTrustBoundary.TargetHash(preparation.Request.Target));

        MutationAuthorizationResult decision = authorizer.Authorize(new MutationAuthorizationRequest(
            MigrationPhase.SandboxDataMigration, MutationClass.SandboxDatabaseWrite, preparation.Request, Owner));

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
            WorkbenchTrustBoundary.TargetHash(request.Target),
            WorkbenchTrustBoundary.TargetHash(new TargetStack { Database = DatabaseTarget.AzureSqlDatabase }));

        // Evidence order is not a difference; evidence content is.
        Assert.Equal(
            WorkbenchTrustBoundary.PlanInputHash(request),
            WorkbenchTrustBoundary.PlanInputHash(request with { Evidence = [.. request.Evidence.Reverse()] }));
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

        public IReadOnlyList<WorkbenchAuthorizationRecord> ForOwner(string ownerId) => Records;
    }

    private sealed class DenyingAuthorizer : IPhaseMutationAuthorizer
    {
        public MutationAuthorizationResult Authorize(MutationAuthorizationRequest request) =>
            MutationAuthorizationResult.Deny("No authorization exists for this run.");
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
