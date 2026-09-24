// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;
using System.Text.Json.Serialization;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Fleet.Execution.Adapters;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// The phase that publishes a generated application tier.
///
/// Every test here is about a refusal or about what is recorded, because the adapter's whole job is to
/// establish that a deployment is attributable before one happens. It builds no image and runs nothing out
/// of the generated tree, so there is no path where it "mostly works": either the bindings hold and a
/// trusted builder is handed the bytes, or nothing is published and the report says why.
/// </summary>
public sealed class AzureSandboxPublishAdapterTests
{
    private const string SourceRoot = "legacy";
    private const string OutputRoot = "out/pilot";
    private const string ReportPath = $"{OutputRoot}/reports/target-deployment.json";
    private const string DigestA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DigestB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ImageDigest = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string ApplicationUrl = "https://ca-ofmfleet-dotnet-dev.jollyground.eastus2.azurecontainerapps.io";
    private const string Subscription = "d4394e57-c076-4c92-a870-5de6bf44f255";
    private const string ResourceGroup = "rg-oracle-forms-migration-fleet-dev-b9f0e875";

    private const string TargetResourceId =
        $"/subscriptions/{Subscription}/resourceGroups/{ResourceGroup}" +
        "/providers/Microsoft.App/containerApps/ca-ofmfleet-dotnet-dev-ykbpnrpd";

    private static readonly DateTimeOffset s_expiry = DateTimeOffset.UtcNow.AddHours(6);

    private static readonly JsonSerializerOptions s_recordJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions s_reportJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// A builder that behaves. It answers with the resource it was asked to update, which is what a real
    /// one does; the tests that care about a misdirected or unnamed deployment use
    /// <see cref="MisreportingGateway"/> instead, so this stub never hides that check.
    /// </summary>
    private sealed class StubGateway(TargetDeploymentResult result) : ITargetApplicationDeploymentGateway
    {
        public string Description => "a stub builder";

        public TargetDeploymentRequest? Received { get; private set; }

        public Task<TargetDeploymentResult> PublishAsync(
            TargetDeploymentRequest request,
            CancellationToken cancellationToken)
        {
            Received = request;

            return Task.FromResult(result.State == TargetDeploymentState.Deployed && result.DeployedResourceId is null
                ? result with { DeployedResourceId = request.Binding.Authority?.TargetResourceId }
                : result);
        }
    }

    /// <summary>A builder whose report is taken at face value, to prove that it is not.</summary>
    private sealed class MisreportingGateway(TargetDeploymentResult result) : ITargetApplicationDeploymentGateway
    {
        public string Description => "a stub builder";

        public Task<TargetDeploymentResult> PublishAsync(
            TargetDeploymentRequest request,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    /// <summary>The server's answer about where this run may publish, as the host would resolve it.</summary>
    private sealed class StubDestination(TargetDeploymentAuthority? authority, params string[] denials)
        : ITargetDeploymentAuthorityProvider
    {
        public int Calls { get; private set; }

        public Task<TargetDeploymentAuthorityDecision> ResolveAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(authority is null
                ? TargetDeploymentAuthorityDecision.Denied(denials)
                : TargetDeploymentAuthorityDecision.Granted(authority));
        }
    }

    /// <summary>Answers with one destination first and a different one afterwards.</summary>
    private sealed class ShiftingDestination(TargetDeploymentAuthority first, TargetDeploymentAuthority second)
        : ITargetDeploymentAuthorityProvider
    {
        private int _calls;

        public Task<TargetDeploymentAuthorityDecision> ResolveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(TargetDeploymentAuthorityDecision.Granted(++_calls == 1 ? first : second));
    }

    private static TargetDeploymentAuthority Destination(
        string resourceId = TargetResourceId,
        DateTimeOffset? expires = null,
        string approvalId = "approval-1") => new()
        {
            TargetProfileId = "profile-1",
            TargetProfileVersion = 3,
            TargetProfileHash = DigestB,
            AzureTenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47",
            SubscriptionId = Subscription,
            ResourceGroup = ResourceGroup,
            TargetResourceId = resourceId,
            ExecutionIdentity = "id-ofmfleet-dotnet-dev-ykbpnrpd",
            EnvironmentName = "dev",
            ApprovalId = approvalId,
            ApprovalExpiresUtc = expires ?? s_expiry,
        };

    private sealed class StubAuthority(GenerationAuthorization? authorization, params string[] denials)
        : IGenerationAuthorizationProvider
    {
        public int Calls { get; private set; }

        public Task<GenerationAuthorizationDecision> AuthorizeAsync(
            GenerationAuthorizationRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(authorization is null
                ? GenerationAuthorizationDecision.Denied(denials)
                : GenerationAuthorizationDecision.Granted(authorization));
        }
    }

    private static MigrationRunRequest Request() => new()
    {
        EngagementId = "ENG-DEPLOY",
        ApplicationName = "Meridian Order Entry",
        RequestedMode = ExecutionMode.SandboxMigration,
        Target = new TargetStack { BackEnd = BackEndStack.AspNetCore, Database = DatabaseTarget.PostgreSql },
        SourceRoot = SourceRoot,
        OutputRoot = OutputRoot,
    };

    private static GenerationAuthorization Authority(
        string scopeDigest = DigestA,
        string runId = "run-1",
        string ledgerId = "ledger-1",
        string tenantId = "tenant-1",
        string projectId = "project-1") => new()
    {
        LedgerId = ledgerId,
        TenantId = tenantId,
        ProjectId = projectId,
        RunId = runId,
        SourceSnapshotHash = DigestA,
        IntermediateContentSha256 = DigestB,
        MappingManifestSha256 = DigestA,
        IssuedUtc = DateTimeOffset.UnixEpoch,
        Scope = [],
        LedgerEntryCount = 0,
        ScopeDigest = scopeDigest,
    };

    /// <summary>Writes a tier and the coverage record that describes it, exactly as generation would.</summary>
    private static TemporaryWorkspace Generated(
        IReadOnlyDictionary<string, string>? files = null,
        string scopeDigest = DigestA,
        string runId = "run-1",
        string sourceSnapshotHash = DigestA)
    {
        TemporaryWorkspace workspace = new();
        IReadOnlyDictionary<string, string> tier = files ?? new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["application/backend/Api/Api.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />",
            ["application/backend/Api/Program.cs"] = "ApiHost.Build(args).Run();",
            ["application/frontend/package.json"] = "{ \"name\": \"generated\" }",
        };

        foreach ((string path, string content) in tier)
        {
            workspace.WriteFile($"{OutputRoot}/{path}", content);
        }

        WorkspaceWriter writer = new(workspace.Root);
        string[] ordered = [.. tier.Keys.Order(StringComparer.Ordinal)];
        (string Path, string ContentSha256)[] hashed =
            [.. ordered.Select(path => (path, writer.Sha256($"{OutputRoot}/{path}", 8L * 1024 * 1024)))];

        workspace.WriteFile(
            $"{OutputRoot}/{GenerationCoverage.RecordPath}",
            JsonSerializer.Serialize(
                new GenerationCoverageRecord(
                    "ledger-1",
                    "tenant-1",
                    "project-1",
                    runId,
                    sourceSnapshotHash,
                    DigestB,
                    DigestA,
                    "fleet.dotnet-master-detail",
                    OutputRoot,
                    GenerationCoverage.OutputSetDigest(hashed),
                    scopeDigest,
                    DateTimeOffset.UnixEpoch,
                    ordered,
                    []),
                s_recordJson));

        return workspace;
    }

    private static PhaseOutcome Verified(PhaseExecutionState state = PhaseExecutionState.Executed) => new(
        MigrationPhase.GeneratedApplicationVerification,
        PhaseStatus.Planned,
        state,
        [],
        [],
        null);

    /// <summary>
    /// The per-entry read of the migrated target. It is the only phase whose results attach to recorded
    /// decisions, so a deployment that claims to publish a verified tier needs it to have executed.
    /// </summary>
    private static PhaseOutcome TargetRead(PhaseExecutionState state = PhaseExecutionState.Executed) => new(
        MigrationPhase.TargetContractVerification,
        PhaseStatus.Planned,
        state,
        [],
        [],
        null);

    private static Task<PhaseExecutionResult> RunAsync(
        TemporaryWorkspace workspace,
        ITargetApplicationDeploymentGateway? gateway,
        IGenerationAuthorizationProvider? authority,
        IReadOnlyList<PhaseOutcome>? completed = null,
        ITargetDeploymentAuthorityProvider? destination = null)
    {
        MigrationRunRequest request = Request();
        PhasePlan plan = MigrationRunPlanner.Plan(request).Phases
            .Single(phase => phase.Phase == MigrationPhase.TargetApplicationDeployment);

        return new AzureSandboxPublishAdapter(gateway).ExecuteAsync(
            new PhaseExecutionContext(workspace.Root, SourceRoot, OutputRoot, plan, request, (_, _) => { })
            {
                CompletedPhases = completed ?? [Verified(), TargetRead()],
                AuthorizationProvider = authority,
                DeploymentAuthorityProvider = destination ?? new StubDestination(Destination()),
            },
            CancellationToken.None);
    }

    private static TargetDeploymentReport Report(TemporaryWorkspace workspace) =>
        JsonSerializer.Deserialize<TargetDeploymentReport>(workspace.Read(ReportPath), s_reportJson)!;

    [Fact]
    public async Task A_verified_tier_is_handed_to_the_builder_and_the_digest_it_returns_is_recorded()
    {
        using TemporaryWorkspace workspace = Generated();
        StubGateway gateway = new(new TargetDeploymentResult(
            TargetDeploymentState.Deployed, "998877", ImageDigest, "rev-0000001", ApplicationUrl, [], null));

        PhaseExecutionResult result = await RunAsync(workspace, gateway, new StubAuthority(Authority()));

        Assert.True(result.Succeeded, result.FailureReason);

        TargetDeploymentReport report = Report(workspace);
        Assert.Equal(TargetDeploymentState.Deployed, report.State);
        Assert.Equal(ImageDigest, report.ImageDigest);
        Assert.Equal(ApplicationUrl, report.ApplicationUrl);
        Assert.Equal("998877", report.BuildRunId);
        Assert.True(TargetDeploymentPolicy.IsContentDigest(report.OperationId));

        // The bytes handed over are the ones the run generated, named by the coverage record.
        Assert.NotNull(gateway.Received);
        Assert.Equal(3, gateway.Received!.Bundle.Files.Count);
        Assert.Contains(gateway.Received.Bundle.Files, file => file.Path == "application/frontend/package.json");
        Assert.Equal("run-1", gateway.Received.Binding.RunId);
    }

    /// <summary>
    /// A deployment is the moment generated code starts answering requests. The report never turns that
    /// into a behavioural claim, because nothing in this run executed the Oracle Forms source.
    /// </summary>
    [Fact]
    public async Task A_successful_deployment_asserts_nothing_about_the_oracle_source()
    {
        using TemporaryWorkspace workspace = Generated();
        PhaseExecutionResult result = await RunAsync(
            workspace,
            new StubGateway(new TargetDeploymentResult(
                TargetDeploymentState.Deployed, "1", ImageDigest, "rev-1", ApplicationUrl, [], null)),
            new StubAuthority(Authority()));

        Assert.True(result.Succeeded);
        Assert.Contains("No behavioral equivalence", Report(workspace).EquivalenceClaim, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Artifacts, artifact => artifact.Kind == ArtifactKind.ExecutableVerificationReport);
    }

    [Fact]
    public async Task Nothing_is_published_when_the_tier_was_never_verified_in_this_run()
    {
        using TemporaryWorkspace workspace = Generated();
        StubGateway gateway = new(new TargetDeploymentResult(
            TargetDeploymentState.Deployed, "1", ImageDigest, "rev-1", ApplicationUrl, [], null));

        PhaseExecutionResult result = await RunAsync(workspace, gateway, new StubAuthority(Authority()), completed: []);

        Assert.False(result.Succeeded);
        Assert.Null(gateway.Received);
        Assert.Contains("did not run in this run", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PhaseExecutionState.Failed)]
    [InlineData(PhaseExecutionState.AdapterNotImplemented)]
    [InlineData(PhaseExecutionState.SkippedByPlanner)]
    [InlineData(PhaseExecutionState.BlockedByDependency)]
    public async Task Nothing_is_published_unless_verification_actually_executed(PhaseExecutionState state)
    {
        using TemporaryWorkspace workspace = Generated();
        StubGateway gateway = new(new TargetDeploymentResult(
            TargetDeploymentState.Deployed, "1", ImageDigest, "rev-1", ApplicationUrl, [], null));

        PhaseExecutionResult result = await RunAsync(
            workspace, gateway, new StubAuthority(Authority()), completed: [Verified(state)]);

        Assert.False(result.Succeeded);
        Assert.Null(gateway.Received);
        Assert.Equal(TargetDeploymentState.NotAttempted, Report(workspace).State);
    }

    /// <summary>
    /// The aggregate suite reports counts. It says how many generated cases ran and not which recorded
    /// decision any of them was about, so on its own it is not evidence that this tier's decisions hold in
    /// the migrated database.
    /// </summary>
    [Fact]
    public async Task The_aggregate_suite_passing_alone_does_not_publish()
    {
        using TemporaryWorkspace workspace = Generated();
        StubGateway gateway = new(new TargetDeploymentResult(
            TargetDeploymentState.Deployed, "1", ImageDigest, "rev-1", ApplicationUrl, [], null));

        PhaseExecutionResult result = await RunAsync(
            workspace, gateway, new StubAuthority(Authority()), completed: [Verified()]);

        Assert.False(result.Succeeded);
        Assert.Null(gateway.Received);
        Assert.Contains("TargetContractVerification did not run", result.FailureReason!, StringComparison.Ordinal);
        Assert.Contains("read the migrated target", result.FailureReason!, StringComparison.Ordinal);
        Assert.Equal(TargetDeploymentState.NotAttempted, Report(workspace).State);
    }

    /// <summary>
    /// A per-entry read that refused — no executed case, a failed case, a claim the server would not
    /// grant — leaves this run with nothing attributable about the migrated target, so nothing publishes.
    /// </summary>
    [Theory]
    [InlineData(PhaseExecutionState.Failed)]
    [InlineData(PhaseExecutionState.AdapterNotImplemented)]
    [InlineData(PhaseExecutionState.SkippedByPlanner)]
    [InlineData(PhaseExecutionState.BlockedByDependency)]
    public async Task Nothing_is_published_unless_the_target_read_actually_executed(PhaseExecutionState state)
    {
        using TemporaryWorkspace workspace = Generated();
        StubGateway gateway = new(new TargetDeploymentResult(
            TargetDeploymentState.Deployed, "1", ImageDigest, "rev-1", ApplicationUrl, [], null));

        PhaseExecutionResult result = await RunAsync(
            workspace, gateway, new StubAuthority(Authority()), completed: [Verified(), TargetRead(state)]);

        Assert.False(result.Succeeded);
        Assert.Null(gateway.Received);
        Assert.Contains($"TargetContractVerification ended in state {state}", result.FailureReason!, StringComparison.Ordinal);
        Assert.Equal(TargetDeploymentState.NotAttempted, Report(workspace).State);
    }

    /// <summary>
    /// The refusal that matters most: bytes edited after generation. An image built from them would carry
    /// the provenance of a generation that did not produce it, which is exactly the false clean result this
    /// product exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_file_edited_after_generation_refuses_the_deployment()
    {
        using TemporaryWorkspace workspace = Generated();
        workspace.WriteFile($"{OutputRoot}/application/backend/Api/Program.cs", "// swapped after the run generated it");

        StubGateway gateway = new(new TargetDeploymentResult(
            TargetDeploymentState.Deployed, "1", ImageDigest, "rev-1", ApplicationUrl, [], null));

        PhaseExecutionResult result = await RunAsync(workspace, gateway, new StubAuthority(Authority()));

        Assert.False(result.Succeeded);
        Assert.Null(gateway.Received);
        Assert.Contains("not the one this run recorded generating", result.FailureReason!, StringComparison.Ordinal);
        Assert.Equal(TargetDeploymentState.RefusedByPolicy, Report(workspace).State);
    }

    [Fact]
    public async Task A_file_removed_after_generation_refuses_the_deployment()
    {
        using TemporaryWorkspace workspace = Generated();
        File.Delete(workspace.Absolute($"{OutputRoot}/application/frontend/package.json"));

        PhaseExecutionResult result = await RunAsync(
            workspace,
            new StubGateway(new TargetDeploymentResult(TargetDeploymentState.Deployed, "1", ImageDigest, "r", ApplicationUrl, [], null)),
            new StubAuthority(Authority()));

        Assert.False(result.Succeeded);
        Assert.Contains("is not in the workspace now", result.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_that_cannot_be_attributed_to_recorded_decisions_is_never_published()
    {
        using TemporaryWorkspace workspace = Generated();
        File.Delete(workspace.Absolute($"{OutputRoot}/{GenerationCoverage.RecordPath}"));

        PhaseExecutionResult result = await RunAsync(
            workspace,
            new StubGateway(new TargetDeploymentResult(TargetDeploymentState.Deployed, "1", ImageDigest, "r", ApplicationUrl, [], null)),
            new StubAuthority(Authority()));

        Assert.False(result.Succeeded);
        Assert.Contains("could not be attributed", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Authority is asked again at the moment of publishing, not carried in from generation. A disposition
    /// revised between writing the code and deploying it is a decision the running application would
    /// otherwise contradict silently.
    /// </summary>
    [Fact]
    public async Task A_decision_revised_after_generation_refuses_the_deployment()
    {
        using TemporaryWorkspace workspace = Generated(scopeDigest: DigestA);
        StubAuthority authority = new(Authority(scopeDigest: DigestB));
        StubGateway gateway = new(new TargetDeploymentResult(
            TargetDeploymentState.Deployed, "1", ImageDigest, "r", ApplicationUrl, [], null));

        PhaseExecutionResult result = await RunAsync(workspace, gateway, authority);

        Assert.Equal(1, authority.Calls);
        Assert.False(result.Succeeded);
        Assert.Null(gateway.Received);
        Assert.Contains("decisions changed after this tier was generated", result.FailureReason!, StringComparison.Ordinal);
        Assert.Equal(TargetDeploymentState.NotAuthorized, Report(workspace).State);
    }

    [Fact]
    public async Task A_coverage_record_naming_another_source_snapshot_refuses_the_deployment()
    {
        using TemporaryWorkspace workspace = Generated(sourceSnapshotHash: DigestB);
        StubGateway gateway = new(new TargetDeploymentResult(
            TargetDeploymentState.Deployed, "1", ImageDigest, "r", ApplicationUrl, [], null));

        PhaseExecutionResult result = await RunAsync(workspace, gateway, new StubAuthority(Authority()));

        Assert.False(result.Succeeded);
        Assert.Null(gateway.Received);
        Assert.Contains("source snapshot", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TargetDeploymentState.NotAuthorized, Report(workspace).State);
    }

    /// <summary>
    /// The coverage record is a document the generation phase wrote into the workspace, so the tenant,
    /// project and ledger it names are compared against the server's own answer rather than trusted. A
    /// record describing another project's generation is a record this authorization is not about.
    /// </summary>
    [Theory]
    [InlineData("ledger-2", "tenant-1", "project-1")]
    [InlineData("ledger-1", "tenant-2", "project-1")]
    [InlineData("ledger-1", "tenant-1", "project-2")]
    public async Task A_coverage_record_naming_another_tenant_project_or_ledger_refuses_the_deployment(
        string ledgerId,
        string tenantId,
        string projectId)
    {
        using TemporaryWorkspace workspace = Generated();
        StubGateway gateway = new(new TargetDeploymentResult(
            TargetDeploymentState.Deployed, "1", ImageDigest, "r", ApplicationUrl, [], null));

        PhaseExecutionResult result = await RunAsync(
            workspace,
            gateway,
            new StubAuthority(Authority(ledgerId: ledgerId, tenantId: tenantId, projectId: projectId)));

        Assert.False(result.Succeeded);
        Assert.Null(gateway.Received);
        Assert.Contains("tenant, project, or disposition ledger", result.FailureReason!, StringComparison.Ordinal);
        Assert.Equal(TargetDeploymentState.NotAuthorized, Report(workspace).State);
    }

    [Fact]
    public async Task A_server_denial_at_the_moment_of_publishing_refuses_the_deployment()
    {
        using TemporaryWorkspace workspace = Generated();

        PhaseExecutionResult result = await RunAsync(
            workspace,
            new StubGateway(new TargetDeploymentResult(TargetDeploymentState.Deployed, "1", ImageDigest, "r", ApplicationUrl, [], null)),
            new StubAuthority(null, "The ledger holds an undecided property."));

        Assert.False(result.Succeeded);
        Assert.Contains("did not authorize", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_phase_with_no_server_authority_refuses_rather_than_publishing_unchecked()
    {
        using TemporaryWorkspace workspace = Generated();

        PhaseExecutionResult result = await RunAsync(
            workspace,
            new StubGateway(new TargetDeploymentResult(TargetDeploymentState.Deployed, "1", ImageDigest, "r", ApplicationUrl, [], null)),
            authority: null);

        Assert.False(result.Succeeded);
        Assert.Equal(TargetDeploymentState.NotAuthorized, Report(workspace).State);
    }

    /// <summary>
    /// An unconfigured host is reported as an unmet host prerequisite, not as a property of the generated
    /// code. The distinction is the whole point: the tier built and passed its tests in this run.
    /// </summary>
    [Fact]
    public async Task A_host_with_no_builder_says_so_instead_of_reporting_a_skipped_deployment()
    {
        using TemporaryWorkspace workspace = Generated();

        PhaseExecutionResult result = await RunAsync(workspace, gateway: null, new StubAuthority(Authority()));

        Assert.False(result.Succeeded);
        Assert.Equal(TargetDeploymentState.GatewayUnavailable, Report(workspace).State);
        Assert.Contains("unmet external prerequisite of the host", result.FailureReason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A builder that says "deployed" without an immutable digest is recorded as unverified. A workflow run
    /// concluding successfully is not, by itself, evidence that anything of this run's is serving.
    /// </summary>
    [Fact]
    public async Task A_builder_claiming_success_without_a_digest_is_recorded_as_unverified()
    {
        using TemporaryWorkspace workspace = Generated();

        PhaseExecutionResult result = await RunAsync(
            workspace,
            new StubGateway(new TargetDeploymentResult(
                TargetDeploymentState.Deployed, "1", ImageDigest: "latest", "rev-1", ApplicationUrl, [], null)),
            new StubAuthority(Authority()));

        Assert.False(result.Succeeded);

        TargetDeploymentReport report = Report(workspace);
        Assert.Equal(TargetDeploymentState.VerificationFailed, report.State);
        Assert.Contains(report.Findings, finding => finding.Contains("immutable image digest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_builder_reporting_a_url_on_an_unexpected_host_is_recorded_as_unverified()
    {
        using TemporaryWorkspace workspace = Generated();

        PhaseExecutionResult result = await RunAsync(
            workspace,
            new StubGateway(new TargetDeploymentResult(
                TargetDeploymentState.Deployed, "1", ImageDigest, "rev-1", "https://attacker.example.com", [], null)),
            new StubAuthority(Authority()));

        Assert.False(result.Succeeded);
        Assert.Equal(TargetDeploymentState.VerificationFailed, Report(workspace).State);
    }

    [Fact]
    public async Task A_build_failure_is_reported_as_a_build_failure_and_not_as_a_missing_capability()
    {
        using TemporaryWorkspace workspace = Generated();

        PhaseExecutionResult result = await RunAsync(
            workspace,
            new StubGateway(TargetDeploymentResult.Refused(
                TargetDeploymentState.BuildFailed, "The generated API did not compile.", ["CS0103 in SubmissionStore.cs"])),
            new StubAuthority(Authority()));

        Assert.False(result.Succeeded);

        TargetDeploymentReport report = Report(workspace);
        Assert.Equal(TargetDeploymentState.BuildFailed, report.State);
        Assert.Contains("CS0103 in SubmissionStore.cs", report.Findings);
    }

    /// <summary>
    /// The operation identity is derived from the bytes and the decisions, so a re-entered run addresses
    /// the same operation instead of starting a second build of identical output.
    /// </summary>
    [Fact]
    public async Task Re_running_the_same_generation_addresses_the_same_operation()
    {
        using TemporaryWorkspace workspace = Generated();
        TargetDeploymentResult deployed = new(
            TargetDeploymentState.Deployed, "1", ImageDigest, "rev-1", ApplicationUrl, [], null);

        StubGateway first = new(deployed);
        StubGateway second = new(deployed);

        await RunAsync(workspace, first, new StubAuthority(Authority()));
        await RunAsync(workspace, second, new StubAuthority(Authority()));

        Assert.Equal(first.Received!.OperationId, second.Received!.OperationId);
    }

    [Fact]
    public async Task A_deployment_bound_to_another_run_is_refused()
    {
        using TemporaryWorkspace workspace = Generated(runId: "run-1");

        PhaseExecutionResult result = await RunAsync(
            workspace,
            new StubGateway(new TargetDeploymentResult(TargetDeploymentState.Deployed, "1", ImageDigest, "r", ApplicationUrl, [], null)),
            new StubAuthority(Authority(runId: "run-2")));

        Assert.False(result.Succeeded);
        Assert.Contains("different run", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The report is read back through the artifact preview, so it must carry no endpoint or secret.</summary>
    [Fact]
    public async Task The_recorded_report_carries_no_registry_subscription_or_credential()
    {
        using TemporaryWorkspace workspace = Generated();

        await RunAsync(
            workspace,
            new StubGateway(new TargetDeploymentResult(
                TargetDeploymentState.Deployed, "1", ImageDigest, "rev-1", ApplicationUrl, [], null)),
            new StubAuthority(Authority()));

        string raw = workspace.Read(ReportPath);

        Assert.False(FleetGuardrails.ContainsPotentialSecret(raw));
        Assert.DoesNotContain("azurecr.io", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("subscriptions/", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("blob.core.windows.net", raw, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The destination comes from the server, never from the workspace. A phase with no authority over it
    /// knows only what the host was configured with, and host configuration is not an approval.
    /// </summary>
    [Fact]
    public async Task A_phase_with_no_authority_over_the_destination_publishes_nowhere()
    {
        using TemporaryWorkspace workspace = Generated();
        StubGateway gateway = new(new TargetDeploymentResult(
            TargetDeploymentState.Deployed, "1", ImageDigest, "rev-1", ApplicationUrl, [], null));

        PhaseExecutionResult result = await RunAsync(
            workspace,
            gateway,
            new StubAuthority(Authority()),
            destination: new StubDestination(null, "No effective approval covers publishing this run."));

        Assert.False(result.Succeeded);
        Assert.Null(gateway.Received);
        Assert.Equal(TargetDeploymentState.NotAuthorized, Report(workspace).State);
    }

    [Fact]
    public async Task The_destination_the_server_resolved_is_carried_to_the_builder_and_into_the_report()
    {
        using TemporaryWorkspace workspace = Generated();
        StubGateway gateway = new(new TargetDeploymentResult(
            TargetDeploymentState.Deployed, "1", ImageDigest, "rev-1", ApplicationUrl, [], null));

        PhaseExecutionResult result = await RunAsync(workspace, gateway, new StubAuthority(Authority()));

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(TargetResourceId, gateway.Received!.Binding.Authority!.TargetResourceId);

        TargetDeploymentDestinationView destination = Report(workspace).Destination!;
        Assert.Equal("ca-ofmfleet-dotnet-dev-ykbpnrpd", destination.ApplicationResourceName);
        Assert.Equal("approval-1", destination.ApprovalId);
        Assert.Equal(3, destination.TargetProfileVersion);
        Assert.Equal(TargetDeploymentPolicy.TargetDigest(Destination()), destination.TargetDigest);
    }

    /// <summary>
    /// An approval that lapsed while the run was working is an approval this run does not publish under.
    /// It is never extended, because extending it would make the expiry a suggestion.
    ///
    /// The summary reason names the rule that refused; which rule it was is a finding, and the report
    /// carries both. The assertion is on the findings because that is where the expiry is stated, not
    /// because the refusal is any softer: the builder is never reached and the report state is NotAuthorized.
    /// </summary>
    [Fact]
    public async Task An_approval_that_expired_before_the_dispatch_publishes_nothing()
    {
        using TemporaryWorkspace workspace = Generated();
        StubGateway gateway = new(new TargetDeploymentResult(
            TargetDeploymentState.Deployed, "1", ImageDigest, "rev-1", ApplicationUrl, [], null));

        PhaseExecutionResult result = await RunAsync(
            workspace,
            gateway,
            new StubAuthority(Authority()),
            destination: new StubDestination(Destination(expires: DateTimeOffset.UtcNow.AddMinutes(-1))));

        Assert.False(result.Succeeded);
        Assert.Null(gateway.Received);

        Assert.Contains(result.Findings, finding =>
            finding.Contains("expired", StringComparison.OrdinalIgnoreCase) &&
            finding.Contains("is not extended", StringComparison.OrdinalIgnoreCase));

        TargetDeploymentReport report = Report(workspace);
        Assert.False(report.Succeeded);
        Assert.Contains(report.Findings, finding => finding.Contains("expired", StringComparison.OrdinalIgnoreCase));
        Assert.Null(report.ImageDigest);
    }

    /// <summary>
    /// The destination is asked for twice: once to bind, once immediately before dispatch. A deployment is
    /// dispatched against one destination or not at all.
    /// </summary>
    [Fact]
    public async Task A_destination_that_changes_between_binding_and_dispatch_publishes_nothing()
    {
        using TemporaryWorkspace workspace = Generated();
        StubGateway gateway = new(new TargetDeploymentResult(
            TargetDeploymentState.Deployed, "1", ImageDigest, "rev-1", ApplicationUrl, [], null));

        PhaseExecutionResult result = await RunAsync(
            workspace,
            gateway,
            new StubAuthority(Authority()),
            destination: new ShiftingDestination(
                Destination(),
                Destination(
                    $"/subscriptions/{Subscription}/resourceGroups/{ResourceGroup}/providers/Microsoft.App/containerApps/ca-somewhere-else",
                    approvalId: "approval-2")));

        Assert.False(result.Succeeded);
        Assert.Null(gateway.Received);
        Assert.Contains("changed between this phase binding it and dispatching", result.FailureReason!, StringComparison.Ordinal);
        Assert.Equal(TargetDeploymentState.NotAuthorized, Report(workspace).State);
    }

    /// <summary>
    /// The builder concluding successfully is not evidence that this run's application is the one serving.
    /// A report naming another resource, or none, is recorded as unverified.
    /// </summary>
    [Fact]
    public async Task A_builder_that_updated_another_resource_is_recorded_as_unverified()
    {
        using TemporaryWorkspace workspace = Generated();

        PhaseExecutionResult result = await RunAsync(
            workspace,
            new MisreportingGateway(new TargetDeploymentResult(
                TargetDeploymentState.Deployed, "1", ImageDigest, "rev-1", ApplicationUrl, [], null)
            {
                DeployedResourceId =
                    $"/subscriptions/{Subscription}/resourceGroups/{ResourceGroup}/providers/Microsoft.App/containerApps/ca-somewhere-else",
            }),
            new StubAuthority(Authority()));

        Assert.False(result.Succeeded);

        TargetDeploymentReport report = Report(workspace);
        Assert.Equal(TargetDeploymentState.VerificationFailed, report.State);
        Assert.Contains(report.Findings, finding => finding.Contains("different Azure resource", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_builder_that_names_no_resource_at_all_is_recorded_as_unverified()
    {
        using TemporaryWorkspace workspace = Generated();

        PhaseExecutionResult result = await RunAsync(
            workspace,
            new MisreportingGateway(new TargetDeploymentResult(
                TargetDeploymentState.Deployed, "1", ImageDigest, "rev-1", ApplicationUrl, [], null)),
            new StubAuthority(Authority()));

        Assert.False(result.Succeeded);
        Assert.Equal(TargetDeploymentState.VerificationFailed, Report(workspace).State);
    }

    /// <summary>
    /// Registration is the difference between a capability and a claim. An executor built the way the host
    /// builds one has to carry this phase, or a run silently reports no deployment step at all.
    /// </summary>
    [Fact]
    public void The_deployment_phase_is_registered_in_the_default_adapter_set()
    {
        Assert.Contains(
            MigrationExecutor.DefaultAdapters(),
            adapter => adapter.Phase == MigrationPhase.TargetApplicationDeployment);

        Assert.Contains(
            MigrationExecutor.DefaultAdapters(deploymentGateway: new StubGateway(
                TargetDeploymentResult.Refused(TargetDeploymentState.NotAttempted, "unused"))),
            adapter => adapter.Phase == MigrationPhase.TargetApplicationDeployment);
    }
}
