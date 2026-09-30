using System.Text.Json;
using OracleFormsMigrationFleet.SourceWorker.Gateway;

namespace OracleFormsMigrationFleet.SourceWorker.Tests;

/// <summary>
/// The gateway either starts with configuration it can defend or it does not start. Everything below is a
/// refusal an operator would rather hit at startup than discover from a served request.
/// </summary>
public sealed class GatewayConfigurationTests
{
    [Fact]
    public void A_registry_binds_a_source_identifier_to_roots_a_request_can_never_widen()
    {
        using GatewayWorkspace workspace = new();

        GatewayOptions options = workspace.Options();
        GatewaySourceEntry? entry = options.Registry.Find(GatewayWorkspace.SourceId);

        Assert.NotNull(entry);
        Assert.Equal(workspace.InputRoot, entry.InputRoot);
        Assert.Equal(workspace.OutputRoot, entry.OutputRoot);
        Assert.Equal(workspace.FormsHome, entry.FormsHome);
        Assert.Equal(GatewayWorkspace.Release, entry.SupportedFormsRelease);
        Assert.Equal(["HRMS", "HRMS_AUDIT"], entry.SchemaAllowlist);
        Assert.Equal(GatewayWorkspace.TenantId, entry.AuthorizedTenantId);
        Assert.Equal([GatewayWorkspace.ProjectId], entry.AuthorizedProjectIds);
        Assert.Null(options.Registry.Find(GatewayWorkspace.OtherSourceId));
    }

    [Fact]
    public void A_registry_entry_that_nests_its_input_and_output_roots_is_refused()
    {
        using GatewayWorkspace workspace = new();
        string nested = Path.Combine(workspace.InputRoot, "artifacts");

        bool parsed = GatewaySourceRegistry.TryParse(
            GatewayWorkspace.RegistryJson(workspace.InputRoot, nested, workspace.FormsHome),
            out _,
            out IReadOnlyList<string> errors);

        Assert.False(parsed);
        Assert.Contains(errors, error => error.Contains("separate trees", StringComparison.Ordinal));
    }

    [Fact]
    public void A_registry_entry_without_an_approved_binary_digest_is_refused()
    {
        string json = JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                sources = new[]
                {
                    new
                    {
                        sourceEnvironmentId = GatewayWorkspace.SourceId,
                        supportedFormsRelease = GatewayWorkspace.Release,
                        inputRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "in")),
                        outputRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "out")),
                        formsHome = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "orant")),
                        approvedLibraryFileVersion = "6.0.8.7.3",
                    },
                },
            },
            GatewayProtocol.Json);

        Assert.False(GatewaySourceRegistry.TryParse(json, out _, out IReadOnlyList<string> errors));
        Assert.Contains(errors, error => error.Contains("approvedLibrarySha256", StringComparison.Ordinal));
    }

    [Fact]
    public void A_registry_that_repeats_a_source_identifier_is_refused()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ofm-registry"));
        object entry = new
        {
            sourceEnvironmentId = GatewayWorkspace.SourceId,
            supportedFormsRelease = GatewayWorkspace.Release,
            inputRoot = Path.Combine(root, "in"),
            outputRoot = Path.Combine(root, "out"),
            formsHome = Path.Combine(root, "orant"),
            approvedLibrarySha256 = GatewayWorkspace.LibrarySha256,
            approvedLibraryFileVersion = "6.0.8.7.3",
            authorizedTenantId = GatewayWorkspace.TenantId,
            authorizedProjectIds = new[] { GatewayWorkspace.ProjectId },
        };

        string json = JsonSerializer.Serialize(new { schemaVersion = 1, sources = new[] { entry, entry } }, GatewayProtocol.Json);

        Assert.False(GatewaySourceRegistry.TryParse(json, out _, out IReadOnlyList<string> errors));
        Assert.Contains(errors, error => error.Contains("repeats", StringComparison.Ordinal));
    }

    [Fact]
    public void A_registry_with_a_relative_root_is_refused()
    {
        string json = JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                sources = new[]
                {
                    new
                    {
                        sourceEnvironmentId = GatewayWorkspace.SourceId,
                        supportedFormsRelease = GatewayWorkspace.Release,
                        inputRoot = "relative/input",
                        outputRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "out")),
                        formsHome = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "orant")),
                        approvedLibrarySha256 = GatewayWorkspace.LibrarySha256,
                        approvedLibraryFileVersion = "6.0.8.7.3",
                    },
                },
            },
            GatewayProtocol.Json);

        Assert.False(GatewaySourceRegistry.TryParse(json, out _, out IReadOnlyList<string> errors));
        Assert.Contains(errors, error => error.Contains("inputRoot", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://source-gateway.contoso.example")]
    [InlineData("http://10.1.2.3:8080")]
    [InlineData("https://source-gateway.contoso.example/source")]
    [InlineData("https://user:pass@source-gateway.contoso.example")]
    public void A_listener_that_would_carry_bearer_tokens_unprotected_is_refused(string url)
    {
        using GatewayWorkspace workspace = new();

        bool read = GatewayOptions.TryRead(
            Reader(workspace.Environment(url)),
            path => File.ReadAllText(path),
            out _,
            out IReadOnlyList<string> errors);

        Assert.False(read);
        Assert.NotEmpty(errors);
    }

    // ---- Unattended service settings ----------------------------------------------------------------

    [Fact]
    public void The_service_name_is_a_constant_so_an_installer_and_a_log_line_cannot_disagree()
    {
        Assert.Equal("OFMSourceGateway", GatewayOptions.ServiceName);
    }

    [Fact]
    public void An_https_listener_defaults_its_certificate_subject_to_the_host_it_binds()
    {
        using GatewayWorkspace workspace = new();

        GatewayOptions options = workspace.Options(values =>
        {
            values[GatewayOptions.UrlVariable] = "https://gateway.ofm.source.internal/";
            values.Remove(GatewayOptions.LoopbackHttpVariable);
        });

        Assert.NotNull(options.Tls);
        Assert.Equal("gateway.ofm.source.internal", options.Tls!.SubjectHost);
        Assert.Null(options.Tls.PinnedThumbprint);
    }

    [Fact]
    public void A_loopback_development_listener_selects_no_certificate_at_all()
    {
        using GatewayWorkspace workspace = new();

        // A thumbprint set against a cleartext listener would suggest a protection it does not have.
        Assert.Null(workspace.Options(values => values[GatewayTlsOptions.ThumbprintVariable] = new string('A', 40)).Tls);
    }

    [Fact]
    public void A_pinned_thumbprint_is_normalized_so_a_pasted_store_value_still_matches()
    {
        using GatewayWorkspace workspace = new();

        GatewayOptions options = workspace.Options(values =>
        {
            values[GatewayOptions.UrlVariable] = "https://gateway.ofm.source.internal/";
            values.Remove(GatewayOptions.LoopbackHttpVariable);
            values[GatewayTlsOptions.ThumbprintVariable] = "ab cd ef 01 23 45 67 89 ab cd ef 01 23 45 67 89 ab cd ef 01";
        });

        Assert.Equal("ABCDEF0123456789ABCDEF0123456789ABCDEF01", options.Tls!.PinnedThumbprint);
    }

    [Theory]
    [InlineData("not-a-thumbprint")]
    [InlineData("ABCDEF")]
    [InlineData("ZZCDEF0123456789ABCDEF0123456789ABCDEF01")]
    public void An_https_listener_with_an_unusable_thumbprint_does_not_start(string thumbprint)
    {
        using GatewayWorkspace workspace = new();
        Dictionary<string, string?> values = workspace.Environment("https://gateway.ofm.source.internal/");
        values.Remove(GatewayOptions.LoopbackHttpVariable);
        values[GatewayTlsOptions.ThumbprintVariable] = thumbprint;

        bool read = GatewayOptions.TryRead(Reader(values), File.ReadAllText, out _, out IReadOnlyList<string> errors);

        Assert.False(read);
        Assert.Contains(errors, error => error.Contains(GatewayTlsOptions.ThumbprintVariable, StringComparison.Ordinal));
    }

    [Fact]
    public void A_protected_credential_root_must_be_a_fully_qualified_directory()
    {
        using GatewayWorkspace workspace = new();
        Dictionary<string, string?> values = workspace.Environment();
        values[GatewayOptions.CredentialRootVariable] = "credentials";

        bool read = GatewayOptions.TryRead(Reader(values), File.ReadAllText, out _, out IReadOnlyList<string> errors);

        Assert.False(read);
        Assert.Contains(errors, error => error.Contains(GatewayOptions.CredentialRootVariable, StringComparison.Ordinal));
    }

    [Fact]
    public void A_configured_credential_root_that_does_not_exist_does_not_degrade_to_environment_fallback()
    {
        using GatewayWorkspace workspace = new();
        Dictionary<string, string?> values = workspace.Environment();
        values[GatewayOptions.CredentialRootVariable] = Path.Combine(workspace.Root, "missing-credentials");

        bool read = GatewayOptions.TryRead(Reader(values), File.ReadAllText, out _, out IReadOnlyList<string> errors);

        Assert.False(read);
        Assert.Contains(errors, error => error.Contains("existing", StringComparison.Ordinal));
    }

    [Fact]
    public void A_gateway_with_no_protected_credential_root_keeps_reading_its_own_environment()
    {
        using GatewayWorkspace workspace = new();
        Assert.Null(workspace.Options().ProtectedCredentialRoot);
    }

    [Fact]
    public void A_loopback_cleartext_listener_needs_an_explicit_opt_in()
    {
        using GatewayWorkspace workspace = new();
        Dictionary<string, string?> values = workspace.Environment("http://127.0.0.1:8080");
        values[GatewayOptions.LoopbackHttpVariable] = null;

        Assert.False(GatewayOptions.TryRead(Reader(values), File.ReadAllText, out _, out IReadOnlyList<string> withoutOptIn));
        Assert.Contains(withoutOptIn, error => error.Contains(GatewayOptions.LoopbackHttpVariable, StringComparison.Ordinal));

        values[GatewayOptions.LoopbackHttpVariable] = "true";
        Assert.True(GatewayOptions.TryRead(Reader(values), File.ReadAllText, out GatewayOptions? options, out _));
        Assert.True(options!.IsLoopbackDevelopmentListener);
    }

    [Fact]
    public void An_https_listener_is_not_a_development_listener()
    {
        using GatewayWorkspace workspace = new();
        GatewayOptions options = workspace.Options(values => values[GatewayOptions.UrlVariable] = "https://0.0.0.0:8443");

        Assert.False(options.IsLoopbackDevelopmentListener);
    }

    [Fact]
    public void Configuration_with_no_allowed_caller_application_is_refused()
    {
        using GatewayWorkspace workspace = new();
        Dictionary<string, string?> values = workspace.Environment();
        values[GatewayOptions.CallerAppIdsVariable] = null;

        Assert.False(GatewayOptions.TryRead(Reader(values), File.ReadAllText, out _, out IReadOnlyList<string> errors));
        Assert.Contains(errors, error => error.Contains("lists no allowed caller", StringComparison.Ordinal));
    }

    [Fact]
    public void A_caller_allowlist_that_is_not_application_identifiers_is_refused()
    {
        using GatewayWorkspace workspace = new();
        Dictionary<string, string?> values = workspace.Environment();
        values[GatewayOptions.CallerAppIdsVariable] = "workbench";

        Assert.False(GatewayOptions.TryRead(Reader(values), File.ReadAllText, out _, out IReadOnlyList<string> errors));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void A_missing_registry_document_refuses_the_whole_configuration()
    {
        using GatewayWorkspace workspace = new();
        Dictionary<string, string?> values = workspace.Environment();
        values[GatewayOptions.RegistryVariable] = Path.Combine(workspace.Root, "absent.json");

        Assert.False(GatewayOptions.TryRead(Reader(values), _ => null, out _, out IReadOnlyList<string> errors));
        Assert.Contains(errors, error => error.Contains("could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public void The_validated_issuers_are_the_configured_tenants_own()
    {
        using GatewayWorkspace workspace = new();
        GatewayOptions options = workspace.Options();

        Assert.Equal(
            [
                $"https://login.microsoftonline.com/{GatewayWorkspace.TenantId}/v2.0",
                $"https://sts.windows.net/{GatewayWorkspace.TenantId}/",
            ],
            options.ValidIssuers);
    }

    [Fact]
    public void Runtime_bounds_are_clamped_rather_than_trusted()
    {
        using GatewayWorkspace workspace = new();
        GatewayOptions options = workspace.Options(values =>
        {
            values[GatewayOptions.ConcurrencyVariable] = "4096";
            values[GatewayOptions.TimeoutVariable] = "86400";
            values[GatewayOptions.OutputBytesVariable] = "1073741824";
        });

        Assert.Equal(8, options.MaxConcurrentExtractions);
        Assert.Equal(TimeSpan.FromMinutes(15), options.ExtractionTimeout);
        Assert.Equal(8 * 1024 * 1024, options.MaxWorkerOutputBytes);
    }

    private static Func<string, string?> Reader(Dictionary<string, string?> values) =>
        key => values.TryGetValue(key, out string? value) ? value : null;
}
