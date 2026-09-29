using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OracleFormsMigrationFleet.SourceWorker.Gateway;

namespace OracleFormsMigrationFleet.SourceWorker.Tests;

/// <summary>
/// The Oracle schema half of the gateway: admission, allowlist narrowing, correlation, digest agreement,
/// the credential reference, and what a child process is allowed to inherit.
///
/// Nothing here opens a database. The one test that starts a real process proves the
/// <c>--extract-schema</c> mode exists, reads a request, and refuses when no Oracle connection is
/// configured — which is exactly what it must do, and is not evidence that Oracle was reached.
/// </summary>
public sealed class GatewaySchemaExtractionTests
{
    private const string ProfileHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public async Task A_registered_source_is_read_and_the_artifact_is_returned_inline()
    {
        using GatewayWorkspace workspace = new();
        StubSchemaExtractionRunner runner = new((_, request) => new GatewaySchemaRunOutcome(
            StubSchemaExtractionRunner.Extracted(request), 0, null));
        using GatewayExtractionCoordinator coordinator = Coordinator(workspace, runner);

        GatewayOracleSchemaOutcome outcome = await ExtractAsync(coordinator, Request());

        Assert.Equal(200, outcome.StatusCode);
        GatewayOracleSchemaResponse response = outcome.Response!;
        Assert.Equal(GatewayStatus.Extracted, response.Status);
        Assert.NotNull(response.SchemaArtifact);
        Assert.Equal(GatewayProtocol.OracleSchemaMediaType, response.SchemaArtifact!.MediaType);
        Assert.Equal(response.SnapshotHash, response.SchemaArtifact.Sha256);

        // The response carries bytes, never a location on this machine.
        byte[] decoded = Convert.FromBase64String(response.SchemaArtifact.Base64);
        Assert.Equal(response.SchemaArtifact.Sha256, ContentHash.OfBytes(decoded));
        Assert.Contains("CREATE TABLE", Encoding.UTF8.GetString(decoded), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_may_narrow_the_registered_allowlist_and_can_never_widen_it()
    {
        using GatewayWorkspace workspace = new();
        StubSchemaExtractionRunner runner = new((_, request) => new GatewaySchemaRunOutcome(
            StubSchemaExtractionRunner.Extracted(request), 0, null));
        using GatewayExtractionCoordinator coordinator = Coordinator(workspace, runner);

        await ExtractAsync(coordinator, Request(schemas: ["hrms", "SYS", "HRMS_AUDIT", "HRMS"]));

        // Case-folded, de-duplicated, ordered, and intersected with the registry. SYS never survives.
        Assert.Equal(["HRMS", "HRMS_AUDIT"], Assert.Single(runner.Requests).SchemaAllowlist);
    }

    [Fact]
    public async Task A_request_naming_only_schemas_this_gateway_does_not_register_reads_nothing()
    {
        using GatewayWorkspace workspace = new();
        StubSchemaExtractionRunner runner = new((_, request) => new GatewaySchemaRunOutcome(
            StubSchemaExtractionRunner.Extracted(request), 0, null));
        using GatewayExtractionCoordinator coordinator = Coordinator(workspace, runner);

        GatewayOracleSchemaOutcome outcome = await ExtractAsync(coordinator, Request(schemas: ["PAYROLL", "SYS"]));

        Assert.Equal(GatewayStatus.Rejected, outcome.Response!.Status);
        Assert.Null(outcome.Response.SchemaArtifact);
        Assert.Equal(0, runner.Invocations);
    }

    [Fact]
    public async Task An_unregistered_source_environment_is_refused_before_anything_is_contacted()
    {
        using GatewayWorkspace workspace = new();
        StubSchemaExtractionRunner runner = new((_, request) => new GatewaySchemaRunOutcome(
            StubSchemaExtractionRunner.Extracted(request), 0, null));
        using GatewayExtractionCoordinator coordinator = Coordinator(workspace, runner);

        GatewayOracleSchemaOutcome outcome = await ExtractAsync(
            coordinator, Request(sourceEnvironmentId: GatewayWorkspace.OtherSourceId));

        Assert.Equal(GatewayStatus.Rejected, outcome.Response!.Status);
        Assert.Equal(0, runner.Invocations);
    }

    [Fact]
    public async Task A_source_with_no_registered_Oracle_connection_is_blocked_and_opens_nothing()
    {
        using GatewayWorkspace workspace = new();
        File.WriteAllText(
            workspace.RegistryPath,
            GatewayWorkspace.RegistryJson(workspace.InputRoot, workspace.OutputRoot, workspace.FormsHome, oracleConnection: false));

        StubSchemaExtractionRunner runner = new((_, request) => new GatewaySchemaRunOutcome(
            StubSchemaExtractionRunner.Extracted(request), 0, null));
        using GatewayExtractionCoordinator coordinator = Coordinator(workspace, runner);

        GatewayOracleSchemaOutcome outcome = await ExtractAsync(coordinator, Request());

        Assert.Equal(GatewayStatus.BlockedPrerequisite, outcome.Response!.Status);
        Assert.Null(outcome.Response.SchemaArtifact);
        Assert.Equal(0, runner.Invocations);
        Assert.Equal("OracleSourceConnection", Assert.Single(outcome.Response.Capabilities).Prerequisite);
    }

    [Theory]
    [InlineData("wrong-source")]
    [InlineData("wrong-profile-version")]
    [InlineData("wrong-allowlist")]
    [InlineData("declared-digest-mismatch")]
    [InlineData("snapshot-digest-mismatch")]
    [InlineData("wrong-media-type")]
    [InlineData("unknown-status")]
    [InlineData("refusal-with-bytes")]
    public async Task A_worker_result_that_is_not_the_one_requested_never_becomes_a_fact_about_the_source(string flaw)
    {
        using GatewayWorkspace workspace = new();
        StubSchemaExtractionRunner runner = new((_, request) =>
        {
            OracleSchemaExtractionResult clean = StubSchemaExtractionRunner.Extracted(request);
            OracleSchemaExtractionResult tampered = flaw switch
            {
                "wrong-source" => clean with { SourceEnvironmentId = GatewayWorkspace.OtherSourceId },
                "wrong-profile-version" => clean with { ProfileVersion = clean.ProfileVersion + 1 },
                "wrong-allowlist" => clean with { SchemaAllowlist = ["HRMS"] },
                "declared-digest-mismatch" => clean with
                {
                    SchemaArtifact = clean.SchemaArtifact! with { Sha256 = new string('d', 64) },
                },
                "snapshot-digest-mismatch" => clean with { SnapshotHash = new string('e', 64) },
                "wrong-media-type" => clean with
                {
                    SchemaArtifact = clean.SchemaArtifact! with { MediaType = "application/json" },
                },
                "unknown-status" => clean with { Status = "Fine" },
                _ => clean with { Status = GatewayStatus.BlockedPrerequisite },
            };

            return new GatewaySchemaRunOutcome(tampered, 0, null);
        });

        using GatewayExtractionCoordinator coordinator = Coordinator(workspace, runner);

        GatewayOracleSchemaOutcome outcome = await ExtractAsync(
            coordinator, Request(schemas: ["HRMS", "HRMS_AUDIT"]));

        Assert.Equal(GatewayStatus.ExtractionFailed, outcome.Response!.Status);
        Assert.Null(outcome.Response.SchemaArtifact);
        Assert.Null(outcome.Response.SnapshotHash);
    }

    [Fact]
    public async Task A_worker_that_produced_no_result_is_reported_as_a_failure_and_not_as_a_refusal()
    {
        using GatewayWorkspace workspace = new();
        StubSchemaExtractionRunner runner = new((_, _) => GatewaySchemaRunOutcome.Failed("The schema worker exceeded its budget."));
        using GatewayExtractionCoordinator coordinator = Coordinator(workspace, runner);

        GatewayOracleSchemaOutcome outcome = await ExtractAsync(coordinator, Request());

        Assert.Equal(GatewayStatus.ExtractionFailed, outcome.Response!.Status);
        Assert.Contains("budget", Assert.Single(outcome.Response.Findings), StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------------
    // The credential reference and the child environment.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_registry_records_a_variable_name_and_never_a_connect_string()
    {
        using GatewayWorkspace workspace = new();
        GatewaySourceEntry entry = workspace.Entry();

        Assert.Equal(GatewayWorkspace.OracleConnectionVariable, entry.OracleCredential!.EnvironmentVariable);

        // Nothing the gateway can print about its own registry can carry the credential.
        string rendered = entry.ToString();
        Assert.DoesNotContain(GatewayWorkspace.OracleConnectionValue, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Pwd=", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            GatewayWorkspace.OracleConnectionValue,
            File.ReadAllText(workspace.RegistryPath),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"connectionString\":\"Dsn=X;Pwd=y\"")]
    [InlineData("\"environmentVariable\":\"AZURE_CLIENT_SECRET\"")]
    [InlineData("\"environmentVariable\":\"ofm_gateway_oracle_lower\"")]
    [InlineData("\"environmentVariable\":\"OFM_GATEWAY_ORACLE_\"")]
    [InlineData("\"environmentVariable\":\"OFM_GATEWAY_ORACLE_A B\"")]
    public void A_registry_that_carries_a_credential_or_points_elsewhere_does_not_start_a_gateway(string oracleConnection)
    {
        using GatewayWorkspace workspace = new();
        string json = GatewayWorkspace
            .RegistryJson(workspace.InputRoot, workspace.OutputRoot, workspace.FormsHome, oracleConnection: false)
            .Replace("\"schemaAllowlist\"", $"\"oracleConnection\":{{{oracleConnection}}},\"schemaAllowlist\"", StringComparison.Ordinal);

        Assert.False(GatewaySourceRegistry.TryParse(json, out _, out IReadOnlyList<string> errors));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void A_schema_child_inherits_operating_system_essentials_and_no_other_variable()
    {
        using GatewayWorkspace workspace = new();
        ProcessStartInfo start = new("dotnet");
        start.Environment["PATH"] = "/usr/bin";
        start.Environment["AZURE_CLIENT_SECRET"] = "a-secret-that-must-not-travel";
        start.Environment["OFM_GATEWAY_ORACLE_OTHER_SOURCE"] = "Dsn=OTHER;Pwd=nope";
        start.Environment[GatewayWorkspace.OracleConnectionVariable] = GatewayWorkspace.OracleConnectionValue;

        ChildProcessSchemaExtractionRunner.BuildEnvironment(
            start,
            workspace.Entry(),
            workspace.Entry().OracleCredential!,
            GatewayWorkspace.OracleConnectionValue,
            TimeSpan.FromMinutes(2));

        Assert.Equal("/usr/bin", start.Environment["PATH"]);
        Assert.False(start.Environment.ContainsKey("AZURE_CLIENT_SECRET"));

        // Neither the source variable nor a sibling entry's variable survives: the child reads the
        // connect string under the worker's own name and has no way to reach another source's.
        Assert.False(start.Environment.ContainsKey("OFM_GATEWAY_ORACLE_OTHER_SOURCE"));
        Assert.False(start.Environment.ContainsKey(GatewayWorkspace.OracleConnectionVariable));

        Assert.Equal(GatewayWorkspace.OracleConnectionValue, start.Environment[OracleSourceConfiguration.ConnectionStringVariable]);
        Assert.Equal("HRMS,HRMS_AUDIT", start.Environment[OracleSourceConfiguration.AllowlistVariable]);
        Assert.Equal("odbc-oracle", start.Environment[OracleSourceConfiguration.ProviderVariable]);
        Assert.Equal("120", start.Environment[OracleSourceConfiguration.TimeoutVariable]);
    }

    [Fact]
    public async Task A_source_whose_connection_variable_is_unset_on_this_host_starts_no_worker()
    {
        using GatewayWorkspace workspace = new();
        ChildProcessSchemaExtractionRunner runner = new(
            workspace.Options(),
            readEnvironment: _ => null,
            launcher: WorkerProcess.Launcher);

        GatewaySchemaRunOutcome outcome = await runner.RunAsync(
            workspace.Entry(), WorkerSchemaRequest(), CancellationToken.None);

        Assert.Null(outcome.Result);
        Assert.Contains(GatewayWorkspace.OracleConnectionVariable, outcome.Failure!, StringComparison.Ordinal);
        Assert.DoesNotContain("Pwd=", outcome.Failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_schema_worker_runs_in_a_child_process_and_its_real_result_is_read_back()
    {
        using GatewayWorkspace workspace = new();

        // A connect string no driver will accept: the point is that --extract-schema exists, parses the
        // request, and answers with a typed document rather than that any database was reached.
        ChildProcessSchemaExtractionRunner runner = new(
            workspace.Options(),
            readEnvironment: name => name == GatewayWorkspace.OracleConnectionVariable ? "Dsn=ofm-no-such-dsn" : null,
            launcher: WorkerProcess.Launcher);

        GatewaySchemaRunOutcome outcome = await runner.RunAsync(
            workspace.Entry(), WorkerSchemaRequest(), CancellationToken.None);

        Assert.Null(outcome.Failure);
        Assert.NotNull(outcome.Result);
        Assert.Equal(GatewayWorkspace.SourceId, outcome.Result!.SourceEnvironmentId);
        Assert.Equal(OracleSchemaProtocol.SchemaVersion, outcome.Result.SchemaVersion);

        // The composed ODBC factory really opened a client: the failure reported names the client, which
        // is only reachable once the connect string has been handed to a real connection. On a Windows
        // host with no such DSN that is the driver manager's OdbcException; on a host with no driver
        // manager at all it is the load fault. Either way there must be no artifact, no success exit,
        // and no connect string anywhere in the document.
        Assert.Contains(outcome.Result.Status, new[] { "ExtractionFailed", CapabilityState.BlockedPrerequisite });
        Assert.Contains("Oracle client", Assert.Single(outcome.Result.Findings), StringComparison.Ordinal);
        Assert.Null(outcome.Result.SchemaArtifact);
        Assert.NotEqual(WorkerExit.Success, outcome.ExitCode);
        Assert.DoesNotContain(
            "ofm-no-such-dsn",
            JsonSerializer.Serialize(outcome.Result, WorkerProtocol.Json),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_schema_output_ceiling_is_configurable_and_bounded()
    {
        using GatewayWorkspace workspace = new();

        Assert.Equal(24 * 1024 * 1024, workspace.Options().MaxSchemaWorkerOutputBytes);
        Assert.Equal(
            32 * 1024 * 1024,
            workspace.Options(values => values[GatewayOptions.SchemaOutputBytesVariable] = "1073741824").MaxSchemaWorkerOutputBytes);
        Assert.Equal(
            64 * 1024,
            workspace.Options(values => values[GatewayOptions.SchemaOutputBytesVariable] = "1").MaxSchemaWorkerOutputBytes);
    }

    private static GatewayExtractionCoordinator Coordinator(
        GatewayWorkspace workspace,
        IGatewaySchemaExtractionRunner schemaRunner) =>
        new(workspace.Options(),
            new StubExtractionRunner((_, _) => GatewayRunOutcome.Failed("not used")),
            () => DateTimeOffset.UtcNow,
            schemaRunner);

    private static GatewayOracleSchemaRequest Request(
        string sourceEnvironmentId = GatewayWorkspace.SourceId,
        string[]? schemas = null) =>
        new(
            GatewayProtocol.SchemaVersion,
            sourceEnvironmentId,
            3,
            ProfileHash,
            schemas ?? ["HRMS", "HRMS_AUDIT"],
            new GatewayAuthorizationScope(GatewayWorkspace.TenantId, GatewayWorkspace.ProjectId));

    private static Task<GatewayOracleSchemaOutcome> ExtractAsync(
        GatewayExtractionCoordinator coordinator,
        GatewayOracleSchemaRequest request) =>
        coordinator.ExtractOracleSchemaAsync(
            new GatewayCallerIdentity(GatewayWorkspace.TenantId, GatewayWorkspace.CallerAppId),
            request,
            CancellationToken.None);

    private static OracleSchemaExtractionRequest WorkerSchemaRequest() =>
        new(OracleSchemaProtocol.SchemaVersion, GatewayWorkspace.SourceId, 3, ProfileHash, ["HRMS"]);
}
