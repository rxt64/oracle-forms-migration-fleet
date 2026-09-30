using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using OracleFormsMigrationFleet.SourceWorker.Gateway;

namespace OracleFormsMigrationFleet.SourceWorker.Tests;

/// <summary>
/// Self-signed certificates built in memory, so the selection rules can be exercised without installing
/// anything into a machine store. They are never trusted by anything; only the selector reads them.
/// </summary>
internal static class GatewayCertificates
{
    public static X509Certificate2 Server(
        string dnsName,
        bool serverAuthentication = true,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new($"CN={dnsName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        SubjectAlternativeNameBuilder alternative = new();
        alternative.AddDnsName(dnsName);
        request.CertificateExtensions.Add(alternative.Build());

        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(serverAuthentication ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")],
            critical: false));

        return request.CreateSelfSigned(
            notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddDays(30));
    }
}

/// <summary>
/// The gateway over a real Kestrel loopback listener with the real JWT bearer handler.
///
/// Signing keys are supplied in process so no tenant metadata is fetched, but nothing else is stubbed:
/// issuer, audience, lifetime and signature are validated by the shipping handler, and the caller
/// application allowlist is enforced by the shipping authorization policy. A test that passed because
/// authentication was switched off would prove nothing about the endpoint that faces the network.
/// </summary>
public sealed class GatewayServerTests
{
    private const string Alias = "hrms_employee.fmb";
    private const string ProfileHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly string s_contentHash = new('b', 64);

    // ---- Server certificate selection ---------------------------------------------------------------
    //
    // An unattended service has nobody to hand a PFX to, so it names a certificate in LocalMachine\My.
    // Every case below is a refusal to listen rather than a fallback: presenting the wrong certificate,
    // an expired one, or one chosen arbitrarily from two candidates is worse than not starting.

    private const string GatewayHost = "gateway.ofm.source.internal";

    [Fact]
    public void A_pinned_thumbprint_selects_exactly_the_certificate_an_operator_approved()
    {
        using X509Certificate2 approved = GatewayCertificates.Server(GatewayHost);
        using X509Certificate2 other = GatewayCertificates.Server(GatewayHost);

        GatewayCertificateSelection selection = GatewayServerCertificate.Select(
            new GatewayTlsOptions(approved.Thumbprint, GatewayHost),
            DateTimeOffset.UtcNow,
            [other, approved]);

        Assert.Null(selection.Failure);
        Assert.Equal(approved.Thumbprint, selection.Certificate!.Thumbprint);
    }

    [Fact]
    public void A_pinned_thumbprint_that_matches_nothing_in_the_store_does_not_fall_back_to_a_host_name_search()
    {
        using X509Certificate2 present = GatewayCertificates.Server(GatewayHost);

        GatewayCertificateSelection selection = GatewayServerCertificate.Select(
            new GatewayTlsOptions(new string('A', 40), GatewayHost),
            DateTimeOffset.UtcNow,
            [present]);

        Assert.Null(selection.Certificate);
        Assert.Contains("thumbprint", selection.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pinned_certificate_for_another_DNS_name_is_refused()
    {
        using X509Certificate2 wrongHost = GatewayCertificates.Server("other.ofm.source.internal");

        GatewayCertificateSelection selection = GatewayServerCertificate.Select(
            new GatewayTlsOptions(wrongHost.Thumbprint, GatewayHost),
            DateTimeOffset.UtcNow,
            [wrongHost]);

        Assert.Null(selection.Certificate);
        Assert.Contains("thumbprint", selection.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_pin_the_listener_host_must_appear_as_a_subject_alternative_name()
    {
        using X509Certificate2 matching = GatewayCertificates.Server(GatewayHost);
        using X509Certificate2 unrelated = GatewayCertificates.Server("workbench.contoso.example");

        GatewayCertificateSelection selection = GatewayServerCertificate.Select(
            new GatewayTlsOptions(null, GatewayHost), DateTimeOffset.UtcNow, [unrelated, matching]);

        Assert.Null(selection.Failure);
        Assert.Equal(matching.Thumbprint, selection.Certificate!.Thumbprint);

        GatewayCertificateSelection wrongHost = GatewayServerCertificate.Select(
            new GatewayTlsOptions(null, "gateway.attacker.example"), DateTimeOffset.UtcNow, [unrelated, matching]);

        Assert.Null(wrongHost.Certificate);
        Assert.Contains("gateway.attacker.example", wrongHost.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wildcard_certificate_is_not_accepted_for_this_listener()
    {
        using X509Certificate2 wildcard = GatewayCertificates.Server("*.ofm.source.internal");

        GatewayCertificateSelection selection = GatewayServerCertificate.Select(
            new GatewayTlsOptions(null, GatewayHost), DateTimeOffset.UtcNow, [wildcard]);

        Assert.Null(selection.Certificate);
    }

    [Fact]
    public void A_certificate_outside_its_validity_window_is_refused()
    {
        using X509Certificate2 expired = GatewayCertificates.Server(
            GatewayHost, notBefore: DateTimeOffset.UtcNow.AddDays(-400), notAfter: DateTimeOffset.UtcNow.AddDays(-1));

        GatewayCertificateSelection selection = GatewayServerCertificate.Select(
            new GatewayTlsOptions(expired.Thumbprint, GatewayHost), DateTimeOffset.UtcNow, [expired]);

        Assert.Null(selection.Certificate);
        Assert.Contains("validity window", selection.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_certificate_with_no_server_authentication_usage_is_refused_rather_than_treated_as_unconstrained()
    {
        using X509Certificate2 clientOnly = GatewayCertificates.Server(GatewayHost, serverAuthentication: false);

        GatewayCertificateSelection selection = GatewayServerCertificate.Select(
            new GatewayTlsOptions(clientOnly.Thumbprint, GatewayHost), DateTimeOffset.UtcNow, [clientOnly]);

        Assert.Null(selection.Certificate);
        Assert.Contains("Server Authentication", selection.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_certificate_whose_private_key_this_account_cannot_use_is_refused()
    {
        using X509Certificate2 withKey = GatewayCertificates.Server(GatewayHost);
        using X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificate(withKey.Export(X509ContentType.Cert));

        GatewayCertificateSelection selection = GatewayServerCertificate.Select(
            new GatewayTlsOptions(publicOnly.Thumbprint, GatewayHost), DateTimeOffset.UtcNow, [publicOnly]);

        Assert.Null(selection.Certificate);
        Assert.Contains("private key", selection.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_usable_certificates_for_the_same_host_are_an_ambiguity_the_gateway_refuses_to_resolve()
    {
        using X509Certificate2 first = GatewayCertificates.Server(GatewayHost);
        using X509Certificate2 second = GatewayCertificates.Server(GatewayHost);

        GatewayCertificateSelection selection = GatewayServerCertificate.Select(
            new GatewayTlsOptions(null, GatewayHost), DateTimeOffset.UtcNow, [first, second]);

        Assert.Null(selection.Certificate);
        Assert.Contains(GatewayTlsOptions.ThumbprintVariable, selection.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_https_host_whose_certificate_cannot_be_selected_refuses_to_build_rather_than_listening()
    {
        using GatewayWorkspace workspace = new();
        GatewayOptions options = workspace.Options(values =>
        {
            values[GatewayOptions.UrlVariable] = $"https://{GatewayHost}/";
            values.Remove(GatewayOptions.LoopbackHttpVariable);
            values[GatewayTlsOptions.ThumbprintVariable] = new string('B', 64);
        });

        GatewayHostBuild build = WorkerGatewayHost.TryBuild(options);

        Assert.Null(build.Application);
        Assert.NotNull(build.Failure);
    }

    [Fact]
    public async Task An_https_host_given_the_approved_certificate_builds()
    {
        using GatewayWorkspace workspace = new();
        using X509Certificate2 approved = GatewayCertificates.Server(GatewayHost);

        GatewayOptions options = workspace.Options(values =>
        {
            values[GatewayOptions.UrlVariable] = $"https://{GatewayHost}/";
            values.Remove(GatewayOptions.LoopbackHttpVariable);
            values[GatewayTlsOptions.ThumbprintVariable] = approved.Thumbprint;
        });

        GatewayHostBuild build = WorkerGatewayHost.TryBuild(options, new GatewayHostDependencies(Certificate: approved));

        Assert.Null(build.Failure);
        Assert.NotNull(build.Application);
        await build.Application!.DisposeAsync();
    }

    [Fact]
    public async Task An_anonymous_request_is_refused()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        using HttpClient client = fixture.Server.Client();

        using HttpResponseMessage response = await client.SendAsync(
            GatewayTestServer.Post(GatewayProtocol.FormsModuleExtractPath, Request(), token: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, fixture.Runner.Invocations);
    }

    [Fact]
    public async Task An_identity_asserted_in_a_header_is_not_an_identity()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        using HttpClient client = fixture.Server.Client();

        using HttpRequestMessage message = GatewayTestServer.Post(GatewayProtocol.FormsModuleExtractPath, Request(), token: null);
        message.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", "44444444-4444-4444-4444-444444444444");
        message.Headers.Add("X-MS-CLIENT-PRINCIPAL-NAME", "workbench");
        message.Headers.Add("X-Forwarded-For", "127.0.0.1");

        using HttpResponseMessage response = await client.SendAsync(message);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, fixture.Runner.Invocations);
    }

    [Fact]
    public async Task A_token_for_another_audience_is_refused()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        using HttpClient client = fixture.Server.Client();

        using HttpResponseMessage response = await client.SendAsync(GatewayTestServer.Post(
            GatewayProtocol.FormsModuleExtractPath, Request(), fixture.Server.Token(audience: "api://some-other-service")));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, fixture.Runner.Invocations);
    }

    [Fact]
    public async Task A_token_from_another_issuer_is_refused()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        using HttpClient client = fixture.Server.Client();

        using HttpResponseMessage response = await client.SendAsync(GatewayTestServer.Post(
            GatewayProtocol.FormsModuleExtractPath,
            Request(),
            fixture.Server.Token(issuer: $"https://login.microsoftonline.com/{GatewayWorkspace.OtherTenantId}/v2.0")));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, fixture.Runner.Invocations);
    }

    [Fact]
    public async Task A_token_signed_by_another_key_is_refused()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        using HttpClient client = fixture.Server.Client();

        using HttpResponseMessage response = await client.SendAsync(GatewayTestServer.Post(
            GatewayProtocol.FormsModuleExtractPath, Request(), fixture.Server.Token(wrongKey: true)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, fixture.Runner.Invocations);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        using HttpClient client = fixture.Server.Client();

        using HttpResponseMessage response = await client.SendAsync(GatewayTestServer.Post(
            GatewayProtocol.FormsModuleExtractPath, Request(), fixture.Server.Token(expired: true)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, fixture.Runner.Invocations);
    }

    [Fact]
    public async Task A_genuine_token_from_a_tenant_this_gateway_does_not_serve_is_refused()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        using HttpClient client = fixture.Server.Client();

        // Signed by the trusted key and issued for the right audience, but the tenant claim is another
        // directory's. Authentication succeeds; the caller policy is what refuses it.
        using HttpResponseMessage response = await client.SendAsync(GatewayTestServer.Post(
            GatewayProtocol.FormsModuleExtractPath, Request(), fixture.Server.Token(tenantId: GatewayWorkspace.OtherTenantId)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, fixture.Runner.Invocations);
    }

    [Fact]
    public async Task A_genuine_token_from_an_unlisted_caller_application_is_refused()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        using HttpClient client = fixture.Server.Client();

        using HttpResponseMessage response = await client.SendAsync(GatewayTestServer.Post(
            GatewayProtocol.FormsModuleExtractPath, Request(), fixture.Server.Token(appId: GatewayWorkspace.UnlistedAppId)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, fixture.Runner.Invocations);
    }

    [Fact]
    public async Task A_token_that_names_no_calling_application_is_refused()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        using HttpClient client = fixture.Server.Client();

        using HttpResponseMessage response = await client.SendAsync(GatewayTestServer.Post(
            GatewayProtocol.FormsModuleExtractPath, Request(), fixture.Server.Token(appId: null)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, fixture.Runner.Invocations);
    }

    [Fact]
    public async Task An_allowlisted_caller_receives_the_artifact_inline_and_no_filesystem_path()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        using HttpClient client = fixture.Server.Client();

        using HttpResponseMessage response = await client.SendAsync(GatewayTestServer.Post(
            GatewayProtocol.FormsModuleExtractPath, Request(), fixture.Server.Token()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        string body = await response.Content.ReadAsStringAsync();
        GatewayFormsModuleResponse? parsed =
            JsonSerializer.Deserialize<GatewayFormsModuleResponse>(body, GatewayProtocol.Json);

        Assert.NotNull(parsed);
        Assert.Equal(GatewayStatus.Extracted, parsed.Status);
        Assert.Equal(GatewayWorkspace.SourceId, parsed.SourceEnvironmentId);
        Assert.Equal(Alias, parsed.ModuleAlias);
        Assert.NotNull(parsed.IntermediateRepresentation);
        Assert.Equal(
            parsed.IntermediateRepresentation.Sha256,
            ContentHash.OfBytes(Convert.FromBase64String(parsed.IntermediateRepresentation.Base64)));

        Assert.DoesNotContain(fixture.Workspace.OutputRoot, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("intermediateRepresentationPath", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.Runner.Invocations);
    }

    [Fact]
    public async Task An_unregistered_source_environment_is_a_typed_refusal_rather_than_an_error()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        using HttpClient client = fixture.Server.Client();

        using HttpResponseMessage response = await client.SendAsync(GatewayTestServer.Post(
            GatewayProtocol.FormsModuleExtractPath,
            Request(GatewayWorkspace.OtherSourceId),
            fixture.Server.Token()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GatewayFormsModuleResponse parsed = JsonSerializer.Deserialize<GatewayFormsModuleResponse>(
            await response.Content.ReadAsStringAsync(), GatewayProtocol.Json)!;

        Assert.Equal(GatewayStatus.Rejected, parsed.Status);
        Assert.Null(parsed.IntermediateRepresentation);
        Assert.Equal(0, fixture.Runner.Invocations);
    }

    [Fact]
    public async Task The_oracle_schema_endpoint_is_authenticated_the_same_way()
    {
        await using Fixture fixture = await Fixture.StartAsync(oracleConnection: false);
        using HttpClient client = fixture.Server.Client();

        object request = new
        {
            schemaVersion = 1,
            sourceEnvironmentId = GatewayWorkspace.SourceId,
            profileVersion = 3,
            profileHash = ProfileHash,
            schemaAllowlist = new[] { "HRMS" },
            scope = new
            {
                tenantId = GatewayWorkspace.TenantId,
                projectId = GatewayWorkspace.ProjectId,
            },
        };

        using HttpResponseMessage anonymous = await client.SendAsync(
            GatewayTestServer.Post(GatewayProtocol.OracleSchemaExtractPath, request, token: null));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using HttpResponseMessage authorized = await client.SendAsync(
            GatewayTestServer.Post(GatewayProtocol.OracleSchemaExtractPath, request, fixture.Server.Token()));
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);

        GatewayOracleSchemaResponse parsed = JsonSerializer.Deserialize<GatewayOracleSchemaResponse>(
            await authorized.Content.ReadAsStringAsync(), GatewayProtocol.Json)!;

        Assert.Equal(GatewayStatus.BlockedPrerequisite, parsed.Status);
        Assert.Null(parsed.SchemaArtifact);
    }

    [Fact]
    public async Task A_body_larger_than_the_protocol_allows_is_refused_before_it_is_parsed()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        using HttpClient client = fixture.Server.Client();

        using HttpRequestMessage message = new(HttpMethod.Post, GatewayProtocol.FormsModuleExtractPath)
        {
            Content = new StringContent(new string('x', GatewayProtocol.MaxRequestBytes * 2), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Server.Token());

        using HttpResponseMessage response = await client.SendAsync(message);

        Assert.True(
            response.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest,
            $"An oversized body produced {(int)response.StatusCode}.");
        Assert.Equal(0, fixture.Runner.Invocations);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_refused()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        using HttpClient client = fixture.Server.Client();

        using HttpRequestMessage message = new(HttpMethod.Post, GatewayProtocol.FormsModuleExtractPath)
        {
            Content = new StringContent("sourceEnvironmentId=legacy", Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Server.Token());

        using HttpResponseMessage response = await client.SendAsync(message);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(0, fixture.Runner.Invocations);
    }

    [Fact]
    public async Task A_malformed_json_body_is_refused_without_reaching_a_worker()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        using HttpClient client = fixture.Server.Client();

        using HttpRequestMessage message = new(HttpMethod.Post, GatewayProtocol.FormsModuleExtractPath)
        {
            Content = new StringContent("{\"schemaVersion\":", Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Server.Token());

        using HttpResponseMessage response = await client.SendAsync(message);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fixture.Runner.Invocations);
    }

    [Fact]
    public async Task An_unknown_route_is_not_served()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        using HttpClient client = fixture.Server.Client();

        using HttpResponseMessage response = await client.SendAsync(
            GatewayTestServer.Post("/source/forms-module/run-command", Request(), fixture.Server.Token()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static object Request(string sourceEnvironmentId = GatewayWorkspace.SourceId) => new
    {
        schemaVersion = GatewayProtocol.SchemaVersion,
        sourceEnvironmentId,
        expectedFormsRelease = GatewayWorkspace.Release,
        profileVersion = 3,
        profileHash = ProfileHash,
        moduleAlias = Alias,
        expectedContentSha256 = s_contentHash,
        scope = new
        {
            tenantId = GatewayWorkspace.TenantId,
            projectId = GatewayWorkspace.ProjectId,
        },
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(GatewayWorkspace workspace, GatewayTestServer server, StubExtractionRunner runner)
        {
            Workspace = workspace;
            Server = server;
            Runner = runner;
        }

        public GatewayWorkspace Workspace { get; }

        public GatewayTestServer Server { get; }

        public StubExtractionRunner Runner { get; }

        public static async Task<Fixture> StartAsync(bool oracleConnection = true)
        {
            GatewayWorkspace workspace = new(oracleConnection);
            StubExtractionRunner runner = new((entry, request) =>
                new GatewayRunOutcome(StubExtractionRunner.Extracted(request, entry.OutputRoot), 0, null));

            GatewayTestServer server = await GatewayTestServer.StartAsync(workspace.Options(), runner);
            return new Fixture(workspace, server, runner);
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            Workspace.Dispose();
        }
    }
}
