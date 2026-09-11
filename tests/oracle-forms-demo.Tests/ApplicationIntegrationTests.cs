using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using OracleFormsDemo;

namespace OracleFormsDemo.Tests;

public sealed class ApplicationIntegrationTests(DemoApplicationFactory factory)
    : IClassFixture<DemoApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Healthz_ReturnsGenericLivenessWithoutOracle()
    {
        var response = await _client.GetAsync("/healthz");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\":\"ok\"", body, StringComparison.Ordinal);
        Assert.Contains("\"database\":\"not-checked\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Oracle", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutBearer_ReturnsUnauthorizedBeforeDatabaseAccess()
    {
        var response = await _client.GetAsync("/api/customer/statement");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEmpty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MalformedLogin_ReturnsBadRequestBeforeDatabaseAccess()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/customer/login",
            new CustomerLoginRequest(0, "valid-length-password"));
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(error);
        Assert.Contains("accountId", error.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManagerEndpoint_WithCustomerSession_RejectsWrongRole()
    {
        var sessions = factory.Services.GetRequiredService<DemoSessionStore>();
        var (token, _) = sessions.Create(SessionRole.Customer, 123456, "Test Customer");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/manager/requests");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnknownBrowserRoute_ReturnsSpaShell()
    {
        var response = await _client.GetAsync("/definitely-not-a-demo-route");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Northstar Online Banking", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownApiRoute_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/definitely-not-a-demo-route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StaticAssets_RequireBrowserRevalidation()
    {
        var response = await _client.GetAsync("/app.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task TransactionReferencePattern_IsCompatibleWithBrowserUnicodeSetRegex()
    {
        var html = await _client.GetStringAsync("/");

        Assert.Contains("pattern=\"[A-Za-z0-9][-A-Za-z0-9_/]*\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("pattern=\"[A-Za-z0-9][A-Za-z0-9/_-]*\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DatabaseHealth_WhenOracleUnavailable_ReturnsGenericServiceUnavailable()
    {
        var response = await _client.GetAsync("/api/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("\"status\":\"degraded\"", body, StringComparison.Ordinal);
        Assert.Contains("\"database\":\"unavailable\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain(DemoApplicationFactory.FakeConnectionString, body, StringComparison.Ordinal);
        Assert.DoesNotContain("LOCAL_ONLY", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exception", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Oracle", body, StringComparison.OrdinalIgnoreCase);
    }
}