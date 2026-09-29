using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OracleFormsMigrationFleet.SourceWorker.Gateway;

namespace OracleFormsMigrationFleet.SourceWorker.Tests;

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
