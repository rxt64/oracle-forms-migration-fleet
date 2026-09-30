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

    // ---- Protected credentials --------------------------------------------------------------------
    //
    // An unattended service must not hold the Oracle connect string in its own environment block. These
    // cover the DPAPI file that replaces it, and specifically the refusals: a protected file that exists
    // but cannot be used must never fall through to an unprotected value.

    [Fact]
    public void A_variable_with_no_protected_file_and_no_environment_value_is_refused_by_name_and_path()
    {
        using GatewayWorkspace workspace = new();
        GatewayCredentialProvider provider = new(workspace.CredentialRoot, readEnvironment: _ => null);

        GatewayCredentialResolution resolution = provider.Resolve(GatewayWorkspace.OracleConnectionVariable);

        Assert.Null(resolution.Value);
        Assert.Contains(GatewayWorkspace.OracleConnectionVariable, resolution.Failure!, StringComparison.Ordinal);
        Assert.Contains(workspace.CredentialRoot, resolution.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_absent_protected_file_falls_back_to_the_environment_because_none_was_provisioned()
    {
        using GatewayWorkspace workspace = new();
        GatewayCredentialProvider provider = new(
            workspace.CredentialRoot,
            readEnvironment: name => name == GatewayWorkspace.OracleConnectionVariable ? GatewayWorkspace.OracleConnectionValue : null);

        GatewayCredentialResolution resolution = provider.Resolve(GatewayWorkspace.OracleConnectionVariable);

        Assert.Equal(GatewayWorkspace.OracleConnectionValue, resolution.Value);
        Assert.Equal("environment", resolution.Source);
    }

    [Fact]
    public void A_protected_file_that_cannot_be_decrypted_is_refused_and_never_falls_back()
    {
        using GatewayWorkspace workspace = new();
        workspace.WriteProtectedCredential(GatewayWorkspace.OracleConnectionVariable, [0x01, 0x02, 0x03, 0x04]);

        // The environment holds a perfectly good value. It must not be used: an operator who provisioned
        // a protected file has to be told it is broken, not quietly served from somewhere else.
        GatewayCredentialProvider provider = new(
            workspace.CredentialRoot,
            readEnvironment: _ => GatewayWorkspace.OracleConnectionValue,
            unprotect: (_, _) => throw new System.Security.Cryptography.CryptographicException("decrypt failed"));

        GatewayCredentialResolution resolution = provider.Resolve(GatewayWorkspace.OracleConnectionVariable);

        Assert.Null(resolution.Value);
        Assert.Equal("protected-file", resolution.Source);
        Assert.Contains(ProtectedFileRefusal, resolution.Failure!, StringComparison.Ordinal);
        Assert.DoesNotContain("Pwd=", resolution.Failure!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("decrypt failed", resolution.Failure!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Dsn=HRMS;Pwd=x\nOFM_WORKER_ORACLE_SCHEMA_ALLOWLIST=SYS")]
    [InlineData("Dsn=HRMS\u0007")]
    public void A_protected_file_that_decrypts_to_something_other_than_one_connect_string_is_refused(string plaintext)
    {
        using GatewayWorkspace workspace = new();
        workspace.WriteProtectedCredential(GatewayWorkspace.OracleConnectionVariable, [0xAA]);

        GatewayCredentialProvider provider = new(
            workspace.CredentialRoot,
            readEnvironment: _ => GatewayWorkspace.OracleConnectionValue,
            unprotect: (_, _) => Encoding.UTF8.GetBytes(plaintext));

        GatewayCredentialResolution resolution = provider.Resolve(GatewayWorkspace.OracleConnectionVariable);

        Assert.Null(resolution.Value);
        Assert.Equal("protected-file", resolution.Source);
        if (plaintext.Trim() is { Length: > 0 } quoted)
        {
            Assert.DoesNotContain(quoted, resolution.Failure!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_protected_file_this_service_account_cannot_open_is_refused_rather_than_treated_as_absent()
    {
        using GatewayWorkspace workspace = new();
        GatewayCredentialProvider provider = new(
            workspace.CredentialRoot,
            readEnvironment: _ => GatewayWorkspace.OracleConnectionValue,
            readProtectedFile: _ => [],
            unprotect: (_, _) => throw new System.Security.Cryptography.CryptographicException());

        Assert.Null(provider.Resolve(GatewayWorkspace.OracleConnectionVariable).Value);
    }

    [Fact]
    public void A_non_file_at_the_protected_path_is_refused_rather_than_treated_as_absent()
    {
        using GatewayWorkspace workspace = new();
        Directory.CreateDirectory(Path.Combine(
            workspace.CredentialRoot,
            GatewayWorkspace.OracleConnectionVariable + GatewayCredentialProvider.ProtectedFileExtension));
        GatewayCredentialProvider provider = new(
            workspace.CredentialRoot,
            readEnvironment: _ => GatewayWorkspace.OracleConnectionValue);

        GatewayCredentialResolution resolution = provider.Resolve(GatewayWorkspace.OracleConnectionVariable);

        Assert.Null(resolution.Value);
        Assert.Equal("protected-file", resolution.Source);
    }

    [Fact]
    public void An_oversized_protected_blob_is_refused_before_it_is_decrypted()
    {
        using GatewayWorkspace workspace = new();
        workspace.WriteProtectedCredential(
            GatewayWorkspace.OracleConnectionVariable,
            new byte[GatewayCredentialProvider.MaxProtectedBlobBytes + 1]);
        int decryptions = 0;
        GatewayCredentialProvider provider = new(
            workspace.CredentialRoot,
            readEnvironment: _ => GatewayWorkspace.OracleConnectionValue,
            unprotect: (_, _) =>
            {
                decryptions++;
                return Encoding.UTF8.GetBytes(GatewayWorkspace.OracleConnectionValue);
            });

        GatewayCredentialResolution resolution = provider.Resolve(GatewayWorkspace.OracleConnectionVariable);

        Assert.Null(resolution.Value);
        Assert.Equal(0, decryptions);
    }

    [Fact]
    public void A_filesystem_link_at_the_protected_path_is_refused_rather_than_followed()
    {
        using GatewayWorkspace workspace = new();
        string target = Path.Combine(workspace.Elsewhere, "protected-target.dpapi");

        // What sits on the far side of the link does not matter, because a reader that refuses links
        // never reaches it. Arbitrary bytes keep this case off DPAPI, which no Linux host has.
        File.WriteAllBytes(target, [0x01, 0x02, 0x03, 0x04]);
        string link = Path.Combine(
            workspace.CredentialRoot,
            GatewayWorkspace.OracleConnectionVariable + GatewayCredentialProvider.ProtectedFileExtension);

        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        int decryptions = 0;
        GatewayCredentialResolution resolution = new GatewayCredentialProvider(
            workspace.CredentialRoot,
            readEnvironment: _ => GatewayWorkspace.OracleConnectionValue,
            unprotect: (_, _) =>
            {
                decryptions++;
                return Encoding.UTF8.GetBytes(GatewayWorkspace.OracleConnectionValue);
            })
            .Resolve(GatewayWorkspace.OracleConnectionVariable);

        Assert.Null(resolution.Value);
        Assert.Equal("protected-file", resolution.Source);

        // Refused for being a link, not for holding something undecryptable: the target was never read.
        Assert.Equal(0, decryptions);
    }

    [Fact]
    public void A_variable_name_outside_the_readable_prefix_resolves_to_nothing_and_names_no_file()
    {
        using GatewayWorkspace workspace = new();
        GatewayCredentialProvider provider = new(workspace.CredentialRoot, readEnvironment: _ => "Dsn=anything");

        Assert.Null(provider.ProtectedPathFor("AZURE_CLIENT_SECRET"));
        Assert.Null(provider.Resolve("AZURE_CLIENT_SECRET").Value);
    }

    [Fact]
    public void The_protected_file_is_derived_from_the_variable_name_and_nothing_else()
    {
        using GatewayWorkspace workspace = new();
        GatewayCredentialProvider provider = new(workspace.CredentialRoot);

        Assert.Equal(
            Path.Combine(workspace.CredentialRoot, GatewayWorkspace.OracleConnectionVariable + GatewayCredentialProvider.ProtectedFileExtension),
            provider.ProtectedPathFor(GatewayWorkspace.OracleConnectionVariable));
    }

    /// <summary>
    /// The real DPAPI round trip, on the only platform that has one. It also proves the blob is bound to
    /// the variable name: a file copied over a sibling's name does not decrypt, so a mistake during
    /// provisioning cannot point one source environment at another source environment's database.
    ///
    /// Off Windows the assertion is the other half of the same rule: a protected file that exists on a
    /// host with no DPAPI is a refusal, not a reason to read the environment instead.
    /// </summary>
    [Fact]
    public void A_credential_protected_on_this_Windows_host_round_trips_and_is_bound_to_its_variable_name()
    {
        using GatewayWorkspace workspace = new();

        if (!OperatingSystem.IsWindows())
        {
            workspace.WriteProtectedCredential(GatewayWorkspace.OracleConnectionVariable, [0x01, 0x02]);
            GatewayCredentialResolution unavailable =
                new GatewayCredentialProvider(workspace.CredentialRoot, readEnvironment: _ => GatewayWorkspace.OracleConnectionValue)
                    .Resolve(GatewayWorkspace.OracleConnectionVariable);

            Assert.Null(unavailable.Value);
            Assert.Contains("DPAPI is a Windows facility", unavailable.Failure!, StringComparison.Ordinal);
            return;
        }

        byte[] blob = GatewayCredentialProvider.Protect(GatewayWorkspace.OracleConnectionVariable, GatewayWorkspace.OracleConnectionValue);
        workspace.WriteProtectedCredential(GatewayWorkspace.OracleConnectionVariable, blob);

        GatewayCredentialProvider provider = new(workspace.CredentialRoot, readEnvironment: _ => null);
        GatewayCredentialResolution resolution = provider.Resolve(GatewayWorkspace.OracleConnectionVariable);

        Assert.Equal(GatewayWorkspace.OracleConnectionValue, resolution.Value);
        Assert.Equal("protected-file", resolution.Source);

        // Same bytes, filed under another registered variable: the entropy no longer matches.
        const string Sibling = "OFM_GATEWAY_ORACLE_OTHER_SOURCE";
        workspace.WriteProtectedCredential(Sibling, blob);
        GatewayCredentialResolution moved = provider.Resolve(Sibling);

        Assert.Null(moved.Value);
        Assert.Contains("could not be decrypted", moved.Failure!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Dsn=HRMS\nPwd=x")]
    [InlineData("Dsn=HRMS\u0007")]
    public void Provisioning_refuses_a_multiline_or_control_shaped_credential(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            GatewayCredentialProvider.Protect(GatewayWorkspace.OracleConnectionVariable, value));
    }

    [Fact]
    public void Provisioning_refuses_a_credential_larger_than_the_reader_accepts()
    {
        Assert.Throws<ArgumentException>(() => GatewayCredentialProvider.Protect(
            GatewayWorkspace.OracleConnectionVariable,
            new string('x', GatewayCredentialProvider.MaxCredentialCharacters + 1)));
    }

    [Fact]
    public void A_schema_runner_resolves_its_credential_through_the_protected_provider()
    {
        using GatewayWorkspace workspace = new();
        GatewayOptions options = workspace.Options(values => values[GatewayOptions.CredentialRootVariable] = workspace.CredentialRoot);

        Assert.Equal(workspace.CredentialRoot, options.ProtectedCredentialRoot);
    }

    [Fact]
    public async Task A_source_whose_protected_credential_is_unusable_starts_no_worker_and_quotes_no_value()
    {
        using GatewayWorkspace workspace = new();
        workspace.WriteProtectedCredential(GatewayWorkspace.OracleConnectionVariable, [0x09, 0x09]);

        ChildProcessSchemaExtractionRunner runner = new(
            workspace.Options(),
            launcher: WorkerProcess.Launcher,
            credentials: new GatewayCredentialProvider(
                workspace.CredentialRoot,
                readEnvironment: _ => GatewayWorkspace.OracleConnectionValue,
                unprotect: (_, _) => throw new System.Security.Cryptography.CryptographicException()));

        GatewaySchemaRunOutcome outcome = await runner.RunAsync(
            workspace.Entry(), WorkerSchemaRequest(), CancellationToken.None);

        Assert.Null(outcome.Result);
        Assert.Null(outcome.ExitCode);
        Assert.Contains(ProtectedFileRefusal, outcome.Failure!, StringComparison.Ordinal);
        Assert.DoesNotContain("Pwd=", outcome.Failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_protected_credential_root_never_reaches_a_schema_child()
    {
        using GatewayWorkspace workspace = new();
        ProcessStartInfo start = new("dotnet");
        start.Environment[GatewayOptions.CredentialRootVariable] = workspace.CredentialRoot;
        start.Environment[GatewayOptions.RegistryVariable] = workspace.RegistryPath;

        ChildProcessSchemaExtractionRunner.BuildEnvironment(
            start,
            workspace.Entry(),
            workspace.Entry().OracleCredential!,
            GatewayWorkspace.OracleConnectionValue,
            TimeSpan.FromMinutes(2));

        // A child that could find the protected root could read every registered source's credential,
        // and a child that could read the registry could learn which variables to look for.
        Assert.False(start.Environment.ContainsKey(GatewayOptions.CredentialRootVariable));
        Assert.False(start.Environment.ContainsKey(GatewayOptions.RegistryVariable));

        // The connect string reaches the child under the worker's own name and under no other.
        Assert.Equal(GatewayWorkspace.OracleConnectionValue, start.Environment[OracleSourceConfiguration.ConnectionStringVariable]);
        Assert.Single(start.Environment, pair => pair.Value == GatewayWorkspace.OracleConnectionValue);
    }

    /// <summary>
    /// The fragment a refused protected credential file carries on this host. DPAPI is a Windows
    /// facility, so off Windows the provider refuses at the missing facility before it attempts any
    /// decryption. The rule under test is the same on both platforms — a protected file that exists but
    /// cannot be used is a refusal and never a fallback to the environment — only the wording differs.
    /// The real decryption path is exercised by the Windows leg of CI.
    /// </summary>
    private static string ProtectedFileRefusal =>
        OperatingSystem.IsWindows() ? "could not be decrypted" : "DPAPI is a Windows facility";

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
