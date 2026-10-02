using System.Diagnostics;
using System.Net;
using System.Text.Json;
using OracleFormsMigrationFleet.SourceWorker.Gateway;

namespace OracleFormsMigrationFleet.SourceWorker.Tests;

/// <summary>
/// The source-environment probe, which is the one gateway call that opens nothing.
///
/// It exists so an operator can ask what this host can actually do before approving anything to extract,
/// and the tests below pin both halves of that: the admission rules are the same ones the extraction
/// paths use, and the answer never promotes a configuration fact into an observation. A registered Oracle
/// connect string proves an operator typed one, not that a database would answer, so the Oracle
/// capability is reported blocked in every path this build can take.
/// </summary>
public sealed class GatewayProbeTests
{
    private const string ProfileHash = "4c1d1d9f0b9b4b4f8f2a1c3d4e5f60718293a4b5c6d7e8f90011223344556677";

    [Fact]
    public async Task A_probe_for_an_unregistered_source_environment_runs_no_worker()
    {
        using GatewayWorkspace workspace = new();
        StubProbeRunner runner = new((_, _) => throw new InvalidOperationException("The probe worker must not run."));
        using GatewayExtractionCoordinator coordinator = Coordinator(workspace, runner);

        GatewayProbeOutcome outcome = await Probe(coordinator, Request() with { SourceEnvironmentId = "not-registered" });

        Assert.Equal(GatewayProbeStatus.Rejected, outcome.Response!.Status);
        Assert.Equal("RegisteredSourceEnvironment", Assert.Single(outcome.Response.Capabilities).Prerequisite);
        Assert.Equal(0, runner.Invocations);
    }

    [Theory]
    [InlineData(GatewayWorkspace.OtherTenantId, GatewayWorkspace.ProjectId)]
    [InlineData(GatewayWorkspace.TenantId, GatewayWorkspace.OtherProjectId)]
    public async Task A_probe_for_a_tenant_or_project_this_gateway_does_not_serve_runs_no_worker(string tenant, string project)
    {
        using GatewayWorkspace workspace = new();
        StubProbeRunner runner = new((_, _) => throw new InvalidOperationException("The probe worker must not run."));
        using GatewayExtractionCoordinator coordinator = Coordinator(workspace, runner);

        GatewayProbeOutcome outcome = await coordinator.ProbeSourceEnvironmentAsync(
            new GatewayCallerIdentity(tenant, GatewayWorkspace.CallerAppId),
            Request() with { Scope = new GatewayAuthorizationScope(tenant, project) },
            CancellationToken.None);

        Assert.Equal(GatewayProbeStatus.Rejected, outcome.Response!.Status);
        Assert.Equal("AuthorizedSourceEnvironmentScope", Assert.Single(outcome.Response.Capabilities).Prerequisite);
        Assert.Equal(0, runner.Invocations);
    }

    [Fact]
    public async Task A_probe_against_an_unapproved_profile_version_runs_no_worker()
    {
        using GatewayWorkspace workspace = new(oracleConnection: true);
        File.WriteAllText(
            workspace.RegistryPath,
            GatewayWorkspace.RegistryJson(
                workspace.InputRoot, workspace.OutputRoot, workspace.FormsHome,
                authorizedProfileVersion: 9));

        StubProbeRunner runner = new((_, _) => throw new InvalidOperationException("The probe worker must not run."));
        using GatewayExtractionCoordinator coordinator = Coordinator(workspace, runner);

        GatewayProbeOutcome outcome = await Probe(coordinator, Request());

        Assert.Equal(GatewayProbeStatus.Rejected, outcome.Response!.Status);
        Assert.Equal("ApprovedSourceProfileVersion", Assert.Single(outcome.Response.Capabilities).Prerequisite);
        Assert.Equal(0, runner.Invocations);
    }

    [Fact]
    public async Task A_probe_expecting_a_different_forms_release_runs_no_worker()
    {
        using GatewayWorkspace workspace = new();
        StubProbeRunner runner = new((_, _) => throw new InvalidOperationException("The probe worker must not run."));
        using GatewayExtractionCoordinator coordinator = Coordinator(workspace, runner);

        GatewayProbeOutcome outcome = await Probe(coordinator, Request() with { ExpectedFormsRelease = "10.1.2.3.0" });

        Assert.Equal(GatewayProbeStatus.Rejected, outcome.Response!.Status);
        Assert.Equal("OracleFormsInstallation", Assert.Single(outcome.Response.Capabilities).Prerequisite);
        Assert.Equal(0, runner.Invocations);
    }

    [Fact]
    public async Task A_worker_result_for_another_request_is_refused_rather_than_reported()
    {
        using GatewayWorkspace workspace = new();
        StubProbeRunner runner = new((_, request) => new GatewayProbeRunOutcome(
            Worker(request) with { SourceEnvironmentId = "a-different-environment" }, WorkerExit.BlockedPrerequisite, null));
        using GatewayExtractionCoordinator coordinator = Coordinator(workspace, runner);

        GatewayProbeOutcome outcome = await Probe(coordinator, Request());

        Assert.Equal(GatewayProbeStatus.Rejected, outcome.Response!.Status);
        Assert.Contains("not correlated", Assert.Single(outcome.Response.Findings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_worker_that_produced_no_result_is_reported_as_a_refusal()
    {
        using GatewayWorkspace workspace = new();
        StubProbeRunner runner = new((_, _) => GatewayProbeRunOutcome.Failed("The probe worker closed its pipes."));
        using GatewayExtractionCoordinator coordinator = Coordinator(workspace, runner);

        GatewayProbeOutcome outcome = await Probe(coordinator, Request());

        Assert.Equal(GatewayProbeStatus.Rejected, outcome.Response!.Status);
        Assert.Contains("closed its pipes", Assert.Single(outcome.Response.Findings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_correlated_worker_result_is_reported_with_the_oracle_prerequisite_still_unmet()
    {
        using GatewayWorkspace workspace = new();
        StubProbeRunner runner = new((_, request) => new GatewayProbeRunOutcome(
            Worker(request), WorkerExit.BlockedPrerequisite, null));
        using GatewayExtractionCoordinator coordinator = Coordinator(workspace, runner);

        GatewayProbeOutcome outcome = await Probe(coordinator, Request());
        GatewayProbeResponse response = outcome.Response!;

        Assert.Equal(200, outcome.StatusCode);
        Assert.Equal(GatewayProbeStatus.BlockedPrerequisite, response.Status);
        Assert.Equal(GatewayWorkspace.SourceId, response.SourceEnvironmentId);
        Assert.Equal(ProfileHash, response.ProfileHash);
        Assert.Equal(GatewayWorkspace.ProjectId, response.Scope!.ProjectId);

        GatewayCapability forms = response.Capabilities.Single(capability => capability.Id == "forms.installation");
        Assert.Equal(GatewayProbeStatus.Verified, forms.State);

        GatewayCapability oracle = response.Capabilities.Single(capability => capability.Id == "oracle.schema.extract");
        Assert.Equal(GatewayProbeStatus.BlockedPrerequisite, oracle.State);
        Assert.Equal("OracleClientConnectivity", oracle.Prerequisite);
        Assert.Equal("operator.verify.oracle.source.connection.on.gateway", oracle.Remediation);
        Assert.Contains("oracleConnectionRegistered=True", oracle.Observed!, StringComparison.Ordinal);

        // No connection was opened and no release family was adjudicated, so nothing is observed here.
        Assert.Null(response.ObservedFormsRelease);
        Assert.Null(response.ObservedDatabaseRelease);
    }

    [Fact]
    public async Task A_source_environment_with_no_registered_oracle_connection_says_so()
    {
        using GatewayWorkspace workspace = new(oracleConnection: false);
        StubProbeRunner runner = new((_, request) => new GatewayProbeRunOutcome(
            Worker(request), WorkerExit.BlockedPrerequisite, null));
        using GatewayExtractionCoordinator coordinator = Coordinator(workspace, runner);

        GatewayProbeOutcome outcome = await Probe(coordinator, Request());

        GatewayCapability oracle = outcome.Response!.Capabilities.Single(capability => capability.Id == "oracle.schema.extract");
        Assert.Equal(GatewayProbeStatus.BlockedPrerequisite, oracle.State);
        Assert.Equal("operator.configure.oracle.source.connection.on.gateway", oracle.Remediation);
        Assert.Contains("oracleConnectionRegistered=False", oracle.Observed!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_gateway_built_without_a_probe_runner_loads_nothing_and_says_so()
    {
        using GatewayWorkspace workspace = new();
        using GatewayExtractionCoordinator coordinator = new(
            workspace.Options(),
            new StubExtractionRunner((_, _) => throw new InvalidOperationException("No extraction here.")));

        GatewayProbeOutcome outcome = await Probe(coordinator, Request());

        Assert.Equal(GatewayProbeStatus.BlockedPrerequisite, outcome.Response!.Status);
        Assert.Contains("without a probe runner", Assert.Single(outcome.Response.Findings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_probe_beyond_the_configured_concurrency_is_refused_rather_than_queued()
    {
        using GatewayWorkspace workspace = new();
        StubProbeRunner runner = new((_, request) => new GatewayProbeRunOutcome(
            Worker(request), WorkerExit.BlockedPrerequisite, null))
        {
            HoldUntilGated = true,
        };

        using GatewayExtractionCoordinator coordinator = new(
            workspace.Options(values => values[GatewayOptions.ConcurrencyVariable] = "1"),
            new StubExtractionRunner((_, _) => throw new InvalidOperationException("No extraction here.")),
            probeRunner: runner);

        Task<GatewayProbeOutcome> first = Probe(coordinator, Request());
        await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        GatewayProbeOutcome refused = await Probe(coordinator, Request());
        Assert.Equal(503, refused.StatusCode);
        Assert.Null(refused.Response);

        runner.Gate.TrySetResult();
        Assert.Equal(GatewayProbeStatus.BlockedPrerequisite, (await first).Response!.Status);
    }

    [Fact]
    public async Task A_cancelled_probe_never_reaches_the_worker()
    {
        using GatewayWorkspace workspace = new();
        StubProbeRunner runner = new((_, _) => throw new InvalidOperationException("The probe worker must not run."));
        using GatewayExtractionCoordinator coordinator = Coordinator(workspace, runner);

        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ProbeSourceEnvironmentAsync(
            Caller(), Request(), cancellation.Token));

        Assert.Equal(0, runner.Invocations);
    }

    [Fact]
    public async Task The_probe_worker_runs_in_a_real_child_process_and_its_result_is_read_back()
    {
        using GatewayWorkspace workspace = new();
        ChildProcessProbeRunner runner = new(workspace.Options(), WorkerProcess.Launcher);

        GatewayProbeRunOutcome outcome = await runner.RunAsync(
            workspace.Entry(),
            new WorkerProbeRequest(WorkerProtocol.SchemaVersion, GatewayWorkspace.SourceId, GatewayWorkspace.Release),
            CancellationToken.None);

        Assert.Null(outcome.Failure);
        Assert.NotNull(outcome.Result);
        Assert.Equal(GatewayWorkspace.SourceId, outcome.Result.SourceEnvironmentId);
        Assert.Equal(GatewayWorkspace.Release, outcome.Result.ExpectedFormsRelease);
        Assert.NotEmpty(outcome.Result.Capabilities);

        // This test host is not a 32-bit Windows worker, so the real worker must refuse rather than claim.
        Assert.Equal(CapabilityState.BlockedPrerequisite, outcome.Result.Status);
        Assert.Equal(WorkerExit.BlockedPrerequisite, outcome.ExitCode);
    }

    [Fact]
    public async Task A_real_child_probe_that_outlives_its_budget_is_terminated_and_reported()
    {
        using GatewayWorkspace workspace = new();
        ChildProcessProbeRunner runner = new(
            workspace.Options() with { ExtractionTimeout = TimeSpan.FromMilliseconds(1) },
            WorkerProcess.Launcher);

        GatewayProbeRunOutcome outcome = await runner.RunAsync(
            workspace.Entry(),
            new WorkerProbeRequest(WorkerProtocol.SchemaVersion, GatewayWorkspace.SourceId, GatewayWorkspace.Release),
            CancellationToken.None);

        Assert.Null(outcome.Result);
        Assert.Contains("time budget", outcome.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_probe_worker_that_cannot_be_started_is_reported_rather_than_thrown()
    {
        using GatewayWorkspace workspace = new();
        ChildProcessProbeRunner runner = new(
            workspace.Options(),
            () => new ProcessStartInfo(Path.Combine(workspace.Root, "no-such-worker-executable")));

        GatewayProbeRunOutcome outcome = await runner.RunAsync(
            workspace.Entry(),
            new WorkerProbeRequest(WorkerProtocol.SchemaVersion, GatewayWorkspace.SourceId, GatewayWorkspace.Release),
            CancellationToken.None);

        Assert.Null(outcome.Result);
        Assert.Contains("could not start", outcome.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_probe_endpoint_authenticates_authorizes_and_answers_over_a_real_listener()
    {
        using GatewayWorkspace workspace = new();
        StubProbeRunner runner = new((_, request) => new GatewayProbeRunOutcome(
            Worker(request), WorkerExit.BlockedPrerequisite, null));

        await using GatewayTestServer server = await GatewayTestServer.StartAsync(
            workspace.Options(),
            new StubExtractionRunner((_, _) => throw new InvalidOperationException("No extraction here.")),
            runner);

        using HttpClient client = server.Client();

        using HttpResponseMessage anonymous = await client.SendAsync(
            GatewayTestServer.Post(GatewayProtocol.SourceEnvironmentProbePath, Request(), null));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using HttpResponseMessage unlisted = await client.SendAsync(
            GatewayTestServer.Post(
                GatewayProtocol.SourceEnvironmentProbePath,
                Request(),
                server.Token(appId: GatewayWorkspace.UnlistedAppId)));
        Assert.Equal(HttpStatusCode.Forbidden, unlisted.StatusCode);

        Assert.Equal(0, runner.Invocations);

        using HttpResponseMessage answered = await client.SendAsync(
            GatewayTestServer.Post(GatewayProtocol.SourceEnvironmentProbePath, Request(), server.Token()));
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);

        GatewayProbeResponse? response = JsonSerializer.Deserialize<GatewayProbeResponse>(
            await answered.Content.ReadAsStringAsync(), GatewayProtocol.Json);

        Assert.Equal(GatewayProbeStatus.BlockedPrerequisite, response!.Status);
        Assert.Equal(GatewayWorkspace.SourceId, response.SourceEnvironmentId);
        Assert.Equal(1, runner.Invocations);
    }

    private static GatewayExtractionCoordinator Coordinator(GatewayWorkspace workspace, IGatewayProbeRunner runner) =>
        new(workspace.Options(),
            new StubExtractionRunner((_, _) => throw new InvalidOperationException("No extraction here.")),
            probeRunner: runner);

    private static Task<GatewayProbeOutcome> Probe(GatewayExtractionCoordinator coordinator, GatewayProbeRequest request) =>
        coordinator.ProbeSourceEnvironmentAsync(Caller(), request, CancellationToken.None);

    private static GatewayCallerIdentity Caller() =>
        new(GatewayWorkspace.TenantId, GatewayWorkspace.CallerAppId);

    private static GatewayProbeRequest Request() => new(
        GatewayProtocol.SchemaVersion,
        GatewayWorkspace.SourceId,
        GatewayWorkspace.Release,
        "9i",
        3,
        ProfileHash,
        new GatewayAuthorizationScope(GatewayWorkspace.TenantId, GatewayWorkspace.ProjectId));

    /// <summary>A worker result shaped the way the real <c>--probe</c> writes one on a provisioned host.</summary>
    private static WorkerProbeResult Worker(WorkerProbeRequest request) => new(
        WorkerProtocol.SchemaVersion,
        request.SourceEnvironmentId,
        "NativeProviderVerified",
        DateTimeOffset.UtcNow,
        "Windows",
        "X86",
        request.ExpectedFormsRelease,
        [new WorkerCapability("forms.installation", CapabilityState.Verified, "OracleFormsInstallation",
            request.ExpectedFormsRelease, "x86", "WindowsWorker", "none", "fileVersion=6.0.8.7.3")]);
}

/// <summary>
/// A probe runner that returns a canned worker result. It stands in for the child process so admission
/// and correlation can be exercised exhaustively; it is never evidence that the native path works, which
/// is what the real-child-process tests beside it are for.
/// </summary>
public sealed class StubProbeRunner(Func<GatewaySourceEntry, WorkerProbeRequest, GatewayProbeRunOutcome> respond)
    : IGatewayProbeRunner
{
    public int Invocations { get; private set; }

    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool HoldUntilGated { get; init; }

    public async Task<GatewayProbeRunOutcome> RunAsync(
        GatewaySourceEntry entry,
        WorkerProbeRequest request,
        CancellationToken cancellationToken)
    {
        Invocations++;
        if (HoldUntilGated)
        {
            Entered.TrySetResult();
            await Gate.Task.WaitAsync(cancellationToken);
        }

        return respond(entry, request);
    }
}
