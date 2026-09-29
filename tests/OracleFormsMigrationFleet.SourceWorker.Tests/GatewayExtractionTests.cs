using System.Diagnostics;
using OracleFormsMigrationFleet.SourceWorker.Gateway;

namespace OracleFormsMigrationFleet.SourceWorker.Tests;

/// <summary>
/// What happens between an authorized request and a response document: registry admission, the worker
/// invocation, the correlation checks, and turning a local artifact into bytes the caller may keep.
/// </summary>
public sealed class GatewayExtractionTests
{
    private const string Alias = "hrms_employee.fmb";
    private const string ProfileHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_request_with_no_source_scope_is_refused_before_gateway_dispatch(bool schema)
    {
        await AssertAuthorizationRefusedAsync(schema, scope: null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Project_B_cannot_dispatch_project_As_registered_source_even_with_the_known_profile_hash(bool schema)
    {
        await AssertAuthorizationRefusedAsync(
            schema,
            new GatewayAuthorizationScope(GatewayWorkspace.TenantId, GatewayWorkspace.OtherProjectId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_scope_for_another_tenant_is_refused_even_when_the_token_tenant_is_authorized(bool schema)
    {
        await AssertAuthorizationRefusedAsync(
            schema,
            new GatewayAuthorizationScope(GatewayWorkspace.OtherTenantId, GatewayWorkspace.ProjectId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_profile_that_does_not_match_the_registry_pin_is_refused_before_gateway_dispatch(bool schema)
    {
        using GatewayWorkspace workspace = new();
        File.WriteAllText(
            workspace.RegistryPath,
            GatewayWorkspace.RegistryJson(
                workspace.InputRoot,
                workspace.OutputRoot,
                workspace.FormsHome,
                authorizedProfileHash: new string('d', 64),
                authorizedProfileVersion: 4));

        await AssertAuthorizationRefusedAsync(schema, Scope(), workspace);
    }

    [Fact]
    public async Task An_unregistered_source_environment_is_refused_without_running_a_worker()
    {
        using GatewayWorkspace workspace = new();
        StubExtractionRunner runner = new((_, _) => throw new InvalidOperationException("The worker must not run."));
        using GatewayExtractionCoordinator coordinator = new(workspace.Options(), runner);

        GatewayFormsModuleOutcome outcome = await ExtractFormsAsync(
            coordinator, Request(sourceEnvironmentId: GatewayWorkspace.OtherSourceId));

        Assert.Equal(200, outcome.StatusCode);
        Assert.Equal(GatewayStatus.Rejected, outcome.Response!.Status);
        Assert.Equal(0, runner.Invocations);
        Assert.Contains("registered", outcome.Response.Findings[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_forms_release_this_gateway_does_not_serve_is_refused()
    {
        using GatewayWorkspace workspace = new();
        StubExtractionRunner runner = new((_, _) => throw new InvalidOperationException("The worker must not run."));
        using GatewayExtractionCoordinator coordinator = new(workspace.Options(), runner);

        GatewayFormsModuleOutcome outcome = await ExtractFormsAsync(coordinator, Request(release: "10.1.2.3"));

        Assert.Equal(GatewayStatus.Rejected, outcome.Response!.Status);
        Assert.Equal(0, runner.Invocations);
        Assert.Equal("OracleFormsInstallation", outcome.Response.Capabilities[0].Prerequisite);
    }

    [Theory]
    [InlineData("../outside.fmb")]
    [InlineData("nested/module.fmb")]
    [InlineData("\\\\server\\share\\module.fmb")]
    [InlineData("C:\\windows\\module.fmb")]
    [InlineData(".hidden.fmb")]
    public async Task An_alias_that_is_not_a_bare_file_name_is_refused_without_running_a_worker(string alias)
    {
        using GatewayWorkspace workspace = new();
        StubExtractionRunner runner = new((_, _) => throw new InvalidOperationException("The worker must not run."));
        using GatewayExtractionCoordinator coordinator = new(workspace.Options(), runner);

        GatewayFormsModuleOutcome outcome = await ExtractFormsAsync(coordinator, Request(alias: alias));

        Assert.Equal(GatewayStatus.Rejected, outcome.Response!.Status);
        Assert.Equal(0, runner.Invocations);
    }

    [Fact]
    public async Task A_request_for_another_protocol_version_is_refused()
    {
        using GatewayWorkspace workspace = new();
        StubExtractionRunner runner = new((_, _) => throw new InvalidOperationException("The worker must not run."));
        using GatewayExtractionCoordinator coordinator = new(workspace.Options(), runner);

        GatewayFormsModuleOutcome outcome = await ExtractFormsAsync(coordinator, Request() with { SchemaVersion = 2 });

        Assert.Equal(GatewayStatus.Rejected, outcome.Response!.Status);
        Assert.Equal(0, runner.Invocations);
    }

    [Fact]
    public async Task An_extraction_is_returned_as_bytes_and_the_local_path_never_leaves_the_gateway()
    {
        using GatewayWorkspace workspace = new();
        StubExtractionRunner runner = new((entry, request) =>
            new GatewayRunOutcome(StubExtractionRunner.Extracted(request, entry.OutputRoot), 0, null));
        using GatewayExtractionCoordinator coordinator = new(workspace.Options(), runner);

        GatewayFormsModuleOutcome outcome = await ExtractFormsAsync(coordinator, Request());

        GatewayFormsModuleResponse response = outcome.Response!;
        Assert.Equal(GatewayStatus.Extracted, response.Status);
        Assert.Equal("HRMS_EMPLOYEE", response.ModuleIdentity);
        Assert.NotNull(response.IntermediateRepresentation);
        Assert.Equal(GatewayProtocol.FormsIrMediaType, response.IntermediateRepresentation.MediaType);

        byte[] decoded = Convert.FromBase64String(response.IntermediateRepresentation.Base64);
        Assert.Equal(response.IntermediateRepresentation.Sha256, ContentHash.OfBytes(decoded));

        string serialized = System.Text.Json.JsonSerializer.Serialize(response, GatewayProtocol.Json);
        Assert.DoesNotContain(workspace.OutputRoot, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("forms-ir.json\"", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Every_correlation_field_is_echoed_so_a_response_cannot_be_replayed()
    {
        using GatewayWorkspace workspace = new();
        StubExtractionRunner runner = new((entry, request) =>
            new GatewayRunOutcome(StubExtractionRunner.Extracted(request, entry.OutputRoot), 0, null));
        using GatewayExtractionCoordinator coordinator = new(workspace.Options(), runner);

        GatewayFormsModuleRequest request = Request();
        GatewayFormsModuleResponse response =
            (await ExtractFormsAsync(coordinator, request)).Response!;

        Assert.Equal(request.SourceEnvironmentId, response.SourceEnvironmentId);
        Assert.Equal(request.ExpectedFormsRelease, response.ExpectedFormsRelease);
        Assert.Equal(request.ProfileVersion, response.ProfileVersion);
        Assert.Equal(request.ProfileHash, response.ProfileHash);
        Assert.Equal(request.ModuleAlias, response.ModuleAlias);
        Assert.Equal(request.ExpectedContentSha256, response.ObservedContentSha256);
    }

    [Fact]
    public async Task An_artifact_outside_the_registered_output_root_is_never_inlined()
    {
        using GatewayWorkspace workspace = new();
        StubExtractionRunner runner = new((_, request) =>
        {
            WorkerExtractionResult result = StubExtractionRunner.Extracted(request, workspace.Elsewhere);
            return new GatewayRunOutcome(result, 0, null);
        });
        using GatewayExtractionCoordinator coordinator = new(workspace.Options(), runner);

        GatewayFormsModuleResponse response =
            (await ExtractFormsAsync(coordinator, Request())).Response!;

        Assert.Equal(GatewayStatus.ExtractionFailed, response.Status);
        Assert.Null(response.IntermediateRepresentation);
        Assert.Contains(response.Findings, finding => finding.Contains("outside the output root", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_artifact_whose_bytes_changed_after_the_worker_hashed_them_is_refused()
    {
        using GatewayWorkspace workspace = new();
        StubExtractionRunner runner = new((entry, request) =>
        {
            WorkerExtractionResult result = StubExtractionRunner.Extracted(request, entry.OutputRoot);
            File.WriteAllText(result.IntermediateRepresentationPath!, "{\"tampered\":true}");
            return new GatewayRunOutcome(result, 0, null);
        });
        using GatewayExtractionCoordinator coordinator = new(workspace.Options(), runner);

        GatewayFormsModuleResponse response =
            (await ExtractFormsAsync(coordinator, Request())).Response!;

        Assert.Equal(GatewayStatus.ExtractionFailed, response.Status);
        Assert.Null(response.IntermediateRepresentation);
        Assert.Contains(response.Findings, finding => finding.Contains("does not hash", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_worker_result_naming_another_source_environment_never_becomes_a_status()
    {
        using GatewayWorkspace workspace = new();
        StubExtractionRunner runner = new((entry, request) =>
        {
            WorkerExtractionResult result = StubExtractionRunner.Extracted(request, entry.OutputRoot)
                with { SourceEnvironmentId = GatewayWorkspace.OtherSourceId };
            return new GatewayRunOutcome(result, 0, null);
        });
        using GatewayExtractionCoordinator coordinator = new(workspace.Options(), runner);

        GatewayFormsModuleResponse response =
            (await ExtractFormsAsync(coordinator, Request())).Response!;

        Assert.Equal(GatewayStatus.ExtractionFailed, response.Status);
        Assert.Null(response.IntermediateRepresentation);
        Assert.Equal(GatewayWorkspace.SourceId, response.SourceEnvironmentId);
    }

    [Fact]
    public async Task A_worker_that_opened_a_different_module_is_reported_as_failed()
    {
        using GatewayWorkspace workspace = new();
        StubExtractionRunner runner = new((entry, request) =>
        {
            WorkerExtractionResult result = StubExtractionRunner.Extracted(request, entry.OutputRoot)
                with { ObservedContentSha256 = new string('f', 64) };
            return new GatewayRunOutcome(result, 0, null);
        });
        using GatewayExtractionCoordinator coordinator = new(workspace.Options(), runner);

        GatewayFormsModuleResponse response =
            (await ExtractFormsAsync(coordinator, Request())).Response!;

        Assert.Equal(GatewayStatus.ExtractionFailed, response.Status);
        Assert.Null(response.IntermediateRepresentation);
    }

    [Fact]
    public async Task A_blocked_worker_result_is_reported_with_its_prerequisite_and_no_artifact()
    {
        using GatewayWorkspace workspace = new();
        StubExtractionRunner runner = new((_, request) =>
            new GatewayRunOutcome(StubExtractionRunner.Blocked(request, "The Forms API is 32-bit and this worker is X64."), 2, null));
        using GatewayExtractionCoordinator coordinator = new(workspace.Options(), runner);

        GatewayFormsModuleResponse response =
            (await ExtractFormsAsync(coordinator, Request())).Response!;

        Assert.Equal(GatewayStatus.BlockedPrerequisite, response.Status);
        Assert.Null(response.IntermediateRepresentation);
        Assert.Equal("WorkerHostArchitecture", response.Capabilities[0].Prerequisite);
        Assert.Equal(CapabilityState.BlockedPrerequisite, response.Capabilities[0].State);
    }

    [Fact]
    public async Task A_worker_that_produced_no_document_is_reported_as_failed_not_blocked()
    {
        using GatewayWorkspace workspace = new();
        StubExtractionRunner runner = new((_, _) => GatewayRunOutcome.Failed("The extraction worker exceeded the gateway's time budget and was terminated."));
        using GatewayExtractionCoordinator coordinator = new(workspace.Options(), runner);

        GatewayFormsModuleResponse response =
            (await ExtractFormsAsync(coordinator, Request())).Response!;

        Assert.Equal(GatewayStatus.ExtractionFailed, response.Status);
        Assert.Contains("time budget", response.Findings[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_extractions_are_bounded_rather_than_queued()
    {
        using GatewayWorkspace workspace = new();
        StubExtractionRunner runner = new(
            (entry, request) => new GatewayRunOutcome(StubExtractionRunner.Extracted(request, entry.OutputRoot), 0, null))
        {
            HoldUntilGated = true,
        };

        using GatewayExtractionCoordinator coordinator = new(workspace.Options(), runner);

        Task<GatewayFormsModuleOutcome> first = ExtractFormsAsync(coordinator, Request());
        while (runner.Invocations == 0)
        {
            await Task.Yield();
        }

        GatewayFormsModuleOutcome second = await ExtractFormsAsync(coordinator, Request());
        Assert.Equal(503, second.StatusCode);
        Assert.Null(second.Response);

        runner.Gate.SetResult();
        Assert.Equal(GatewayStatus.Extracted, (await first).Response!.Status);
    }

    [Fact]
    public async Task The_oracle_schema_endpoint_reports_a_typed_blocked_prerequisite_and_opens_nothing()
    {
        using GatewayWorkspace workspace = new(oracleConnection: false);
        StubExtractionRunner runner = new((_, _) => throw new InvalidOperationException("The worker must not run."));
        using GatewayExtractionCoordinator coordinator = new(workspace.Options(), runner);

        GatewayOracleSchemaOutcome outcome = await ExtractSchemaAsync(
            coordinator,
            new GatewayOracleSchemaRequest(
                1,
                GatewayWorkspace.SourceId,
                3,
                ProfileHash,
                ["HRMS", "PAYROLL"],
                Scope()));

        GatewayOracleSchemaResponse response = outcome.Response!;
        Assert.Equal(GatewayStatus.BlockedPrerequisite, response.Status);
        Assert.Null(response.SchemaArtifact);
        Assert.Equal("OracleSourceConnection", response.Capabilities[0].Prerequisite);

        Assert.Equal("registeredSchemas=2;oracleConnectionRegistered=False", response.Capabilities[0].Observed);
        Assert.Equal(0, runner.Invocations);
    }

    [Fact]
    public async Task The_oracle_schema_endpoint_refuses_an_unregistered_source_environment()
    {
        using GatewayWorkspace workspace = new();
        StubExtractionRunner runner = new((_, _) => throw new InvalidOperationException("The worker must not run."));
        using GatewayExtractionCoordinator coordinator = new(workspace.Options(), runner);

        GatewayOracleSchemaOutcome outcome = await ExtractSchemaAsync(
            coordinator,
            new GatewayOracleSchemaRequest(
                1,
                GatewayWorkspace.OtherSourceId,
                3,
                ProfileHash,
                ["HRMS"],
                Scope()));

        Assert.Equal(GatewayStatus.Rejected, outcome.Response!.Status);
    }

    [Fact]
    public async Task A_link_under_the_output_root_is_refused_rather_than_followed()
    {
        using GatewayWorkspace workspace = new();
        string target = Path.Combine(workspace.Elsewhere, "target.forms-ir.json");
        File.WriteAllText(target, "{}");
        string link = Path.Combine(workspace.OutputRoot, "linked.forms-ir.json");

        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Creating links is a privileged operation on some hosts; the containment rule is covered by
            // the outside-the-root test either way.
            return;
        }

        GatewayArtifactResult result = await GatewayArtifactInliner.InlineAsync(
            workspace.OutputRoot, link, null, GatewayProtocol.FormsIrMediaType, CancellationToken.None);

        Assert.Null(result.Artifact);
        Assert.Contains("link", result.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_absent_artifact_is_refused_rather_than_reported_empty()
    {
        using GatewayWorkspace workspace = new();

        GatewayArtifactResult result = await GatewayArtifactInliner.InlineAsync(
            workspace.OutputRoot,
            Path.Combine(workspace.OutputRoot, "absent.forms-ir.json"),
            null,
            GatewayProtocol.FormsIrMediaType,
            CancellationToken.None);

        Assert.Null(result.Artifact);
        Assert.Contains("not present", result.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_worker_runs_in_a_child_process_and_its_real_result_is_read_back()
    {
        using GatewayWorkspace workspace = new();
        ChildProcessExtractionRunner runner = new(workspace.Options(), WorkerProcess.Launcher);

        GatewayRunOutcome outcome = await runner.RunAsync(
            workspace.Entry(), WorkerRequest(), CancellationToken.None);

        Assert.Null(outcome.Failure);
        Assert.NotNull(outcome.Result);
        Assert.Equal(WorkerExit.BlockedPrerequisite, outcome.ExitCode);
        Assert.Equal(CapabilityState.BlockedPrerequisite, outcome.Result.Status);
        Assert.Equal(GatewayWorkspace.SourceId, outcome.Result.SourceEnvironmentId);
        Assert.Null(outcome.Result.IntermediateRepresentationPath);
    }

    [Fact]
    public async Task A_child_process_that_outlives_its_budget_is_terminated_and_reported()
    {
        using GatewayWorkspace workspace = new();
        GatewayOptions options = workspace.Options() with { ExtractionTimeout = TimeSpan.FromMilliseconds(1) };

        ChildProcessExtractionRunner runner = new(options, WorkerProcess.Launcher);
        GatewayRunOutcome outcome = await runner.RunAsync(workspace.Entry(), WorkerRequest(), CancellationToken.None);

        Assert.Null(outcome.Result);
        Assert.Contains("time budget", outcome.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_child_process_that_writes_past_the_output_cap_is_terminated_and_reported()
    {
        using GatewayWorkspace workspace = new();
        GatewayOptions options = workspace.Options() with { MaxWorkerOutputBytes = 16 };

        ChildProcessExtractionRunner runner = new(options, WorkerProcess.Launcher);
        GatewayRunOutcome outcome = await runner.RunAsync(workspace.Entry(), WorkerRequest(), CancellationToken.None);

        Assert.Null(outcome.Result);
        Assert.Contains("more output than the gateway accepts", outcome.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_worker_that_cannot_be_started_is_reported_rather_than_thrown()
    {
        using GatewayWorkspace workspace = new();
        ChildProcessExtractionRunner runner = new(
            workspace.Options(),
            () => new ProcessStartInfo(Path.Combine(workspace.Root, "no-such-worker-executable")));

        GatewayRunOutcome outcome = await runner.RunAsync(workspace.Entry(), WorkerRequest(), CancellationToken.None);

        Assert.Null(outcome.Result);
        Assert.Contains("could not start", outcome.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_forms_child_inherits_operating_system_essentials_and_no_credential_of_this_host()
    {
        using GatewayWorkspace workspace = new();
        GatewaySourceEntry entry = workspace.Entry();

        ProcessStartInfo start = new("dotnet");

        start.Environment["PATH"] = "/usr/bin";
        start.Environment["SystemRoot"] = @"C:\Windows";
        start.Environment["windir"] = @"C:\Windows";
        start.Environment["ProgramFiles(x86)"] = @"C:\Program Files (x86)";
        start.Environment["DOTNET_ROOT"] = @"C:\dotnet";
        start.Environment["NLS_LANG"] = "AMERICAN_AMERICA.WE8MSWIN1252";

        start.Environment[GatewayWorkspace.OracleConnectionVariable] = GatewayWorkspace.OracleConnectionValue;
        start.Environment["OFM_GATEWAY_ORACLE_OTHER_SOURCE"] = "Dsn=OTHER;Pwd=also-not-real";
        start.Environment["AZURE_CLIENT_SECRET"] = "a-secret-that-must-not-travel";
        start.Environment[GatewayOptions.RegistryVariable] = workspace.RegistryPath;
        string[] databaseSettings = ["ORACLE_HOME", "TNS_ADMIN", "ODBCINI", "ODBCSYSINI"];
        foreach (string setting in databaseSettings)
        {
            start.Environment[setting] = workspace.Elsewhere;
        }

        ChildProcessExtractionRunner.BuildEnvironment(start, entry, TimeSpan.FromMinutes(2));

        Assert.Equal("/usr/bin", start.Environment["PATH"]);
        Assert.Equal(@"C:\Windows", start.Environment["SystemRoot"]);
        Assert.Equal(@"C:\Windows", start.Environment["windir"]);
        Assert.Equal(@"C:\Program Files (x86)", start.Environment["ProgramFiles(x86)"]);
        Assert.Equal(@"C:\dotnet", start.Environment["DOTNET_ROOT"]);
        Assert.Equal("AMERICAN_AMERICA.WE8MSWIN1252", start.Environment["NLS_LANG"]);

        Assert.False(start.Environment.ContainsKey(GatewayWorkspace.OracleConnectionVariable));
        Assert.False(start.Environment.ContainsKey("OFM_GATEWAY_ORACLE_OTHER_SOURCE"));
        Assert.False(start.Environment.ContainsKey("AZURE_CLIENT_SECRET"));
        Assert.False(start.Environment.ContainsKey(GatewayOptions.RegistryVariable));
        foreach (string setting in databaseSettings)
        {
            Assert.False(start.Environment.ContainsKey(setting));
        }
        Assert.DoesNotContain(
            start.Environment.Values,
            value => value is not null && value.Contains("Pwd=", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(workspace.InputRoot, start.Environment[WorkerConfiguration.InputRootVariable]);
        Assert.Equal(workspace.OutputRoot, start.Environment[WorkerConfiguration.OutputRootVariable]);
        Assert.Equal(workspace.FormsHome, start.Environment[WorkerConfiguration.FormsHomeVariable]);
        Assert.Equal(GatewayWorkspace.LibrarySha256, start.Environment[WorkerConfiguration.LibraryHashVariable]);
        Assert.Equal("6.0.8.7.3", start.Environment[WorkerConfiguration.LibraryVersionVariable]);
        Assert.Equal("120", start.Environment[WorkerConfiguration.TimeoutVariable]);
    }

    [Fact]
    public void A_forms_child_environment_is_rebuilt_from_scratch_for_every_source_entry()
    {
        using GatewayWorkspace workspace = new();
        ProcessStartInfo start = new("dotnet");

        start.Environment[WorkerConfiguration.InputRootVariable] = workspace.Elsewhere;
        start.Environment[WorkerConfiguration.FormsHomeVariable] = workspace.Elsewhere;
        start.Environment[WorkerConfiguration.LibraryHashVariable] = new string('f', 64);
        start.Environment[OracleSourceConfiguration.ConnectionStringVariable] = "Dsn=STALE;Pwd=also-not-real";
        start.Environment[OracleSourceConfiguration.AllowlistVariable] = "SOMEONE_ELSE";

        ChildProcessExtractionRunner.BuildEnvironment(start, workspace.Entry(), TimeSpan.FromMinutes(2));

        Assert.Equal(workspace.InputRoot, start.Environment[WorkerConfiguration.InputRootVariable]);
        Assert.Equal(workspace.FormsHome, start.Environment[WorkerConfiguration.FormsHomeVariable]);
        Assert.Equal(GatewayWorkspace.LibrarySha256, start.Environment[WorkerConfiguration.LibraryHashVariable]);
        Assert.False(start.Environment.ContainsKey(OracleSourceConfiguration.ConnectionStringVariable));
        Assert.False(start.Environment.ContainsKey(OracleSourceConfiguration.AllowlistVariable));
    }

    private static GatewayFormsModuleRequest Request(
        string sourceEnvironmentId = GatewayWorkspace.SourceId,
        string release = GatewayWorkspace.Release,
        string alias = Alias) =>
        new(
            GatewayProtocol.SchemaVersion,
            sourceEnvironmentId,
            release,
            3,
            ProfileHash,
            alias,
            new string('b', 64),
            Scope());

    private static GatewayAuthorizationScope Scope() =>
        new(GatewayWorkspace.TenantId, GatewayWorkspace.ProjectId);

    private static GatewayCallerIdentity Caller() =>
        new(GatewayWorkspace.TenantId, GatewayWorkspace.CallerAppId);

    private static Task<GatewayFormsModuleOutcome> ExtractFormsAsync(
        GatewayExtractionCoordinator coordinator,
        GatewayFormsModuleRequest request) =>
        coordinator.ExtractFormsModuleAsync(Caller(), request, CancellationToken.None);

    private static Task<GatewayOracleSchemaOutcome> ExtractSchemaAsync(
        GatewayExtractionCoordinator coordinator,
        GatewayOracleSchemaRequest request) =>
        coordinator.ExtractOracleSchemaAsync(Caller(), request, CancellationToken.None);

    private static async Task AssertAuthorizationRefusedAsync(
        bool schema,
        GatewayAuthorizationScope? scope,
        GatewayWorkspace? suppliedWorkspace = null)
    {
        bool ownsWorkspace = suppliedWorkspace is null;
        GatewayWorkspace workspace = suppliedWorkspace ?? new GatewayWorkspace();
        try
        {
            StubExtractionRunner formsRunner = new((_, _) =>
                throw new InvalidOperationException("The Forms worker must not run."));
            StubSchemaExtractionRunner schemaRunner = new((_, _) =>
                throw new InvalidOperationException("The schema worker must not run."));
            using GatewayExtractionCoordinator coordinator = new(
                workspace.Options(), formsRunner, schemaRunner: schemaRunner);

            string prerequisite;
            if (schema)
            {
                GatewayOracleSchemaOutcome outcome = await ExtractSchemaAsync(
                    coordinator,
                    new GatewayOracleSchemaRequest(
                        GatewayProtocol.SchemaVersion,
                        GatewayWorkspace.SourceId,
                        3,
                        ProfileHash,
                        ["HRMS"],
                        scope));
                Assert.Equal(GatewayStatus.Rejected, outcome.Response!.Status);
                prerequisite = Assert.Single(outcome.Response.Capabilities).Prerequisite;
            }
            else
            {
                GatewayFormsModuleOutcome outcome = await ExtractFormsAsync(
                    coordinator,
                    Request() with { Scope = scope });
                Assert.Equal(GatewayStatus.Rejected, outcome.Response!.Status);
                prerequisite = Assert.Single(outcome.Response.Capabilities).Prerequisite;
            }

            Assert.Contains(
                prerequisite,
                new[] { "AuthorizedSourceEnvironmentScope", "ApprovedSourceProfileVersion" });
            Assert.Equal(0, formsRunner.Invocations);
            Assert.Equal(0, schemaRunner.Invocations);
        }
        finally
        {
            if (ownsWorkspace)
            {
                workspace.Dispose();
            }
        }
    }

    private static WorkerExtractionRequest WorkerRequest() =>
        new(WorkerProtocol.SchemaVersion, GatewayWorkspace.SourceId, GatewayWorkspace.Release, 3, ProfileHash, Alias, new string('b', 64));
}
