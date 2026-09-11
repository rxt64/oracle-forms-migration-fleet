using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

public class FoundryAgentClientTests
{
    [Fact]
    public void Accepts_exact_https_hosted_agent_responses_endpoint()
    {
        const string value = "https://account.services.ai.azure.com/api/projects/project/agents/fleet/endpoint/protocols/openai/responses?api-version=v1";

        bool valid = FoundryAgentClient.TryParseEndpoint(value, out Uri? endpoint);

        Assert.True(valid);
        Assert.Equal(value, endpoint!.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://account.services.ai.azure.com/api/projects/project/agents/fleet/endpoint/protocols/openai/responses")]
    [InlineData("https://example.com/api/projects/project/agents/fleet/endpoint/protocols/openai/responses")]
    [InlineData("https://account.services.ai.azure.com/api/projects/project/models/gpt/responses")]
    [InlineData("https://user@account.services.ai.azure.com/api/projects/project/agents/fleet/endpoint/protocols/openai/responses")]
    [InlineData("https://account.services.ai.azure.com:8443/api/projects/project/agents/fleet/endpoint/protocols/openai/responses")]
    [InlineData("https://account.services.ai.azure.com/api/projects/project/agents/fleet/endpoint/protocols/openai/responses#fragment")]
    [InlineData("https://account.services.ai.azure.com/api/projects/project/agents/fleet/endpoint/protocols/openai/responses?api-version=v1&redirect=https://example.com")]
    [InlineData("https://evil.account.services.ai.azure.com/api/projects/project/agents/fleet/endpoint/protocols/openai/responses")]
    [InlineData("https://account.services.ai.azure.com/api/projects/project/agents/fleet/extra/endpoint/protocols/openai/responses")]
    [InlineData("https://account.services.ai.azure.com/tenants/x/api/projects/project/agents/fleet/endpoint/protocols/openai/responses")]
    [InlineData("https://account.services.ai.azure.com/api/projects/project%2Fagents%2Ffleet/agents/fleet/endpoint/protocols/openai/responses")]
    [InlineData("")]
    public void Rejects_untrusted_or_non_agent_endpoints(string value)
    {
        Assert.False(FoundryAgentClient.TryParseEndpoint(value, out Uri? endpoint));
        Assert.Null(endpoint);
    }
}