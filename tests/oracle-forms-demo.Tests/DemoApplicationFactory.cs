using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace OracleFormsDemo.Tests;

public sealed class DemoApplicationFactory : WebApplicationFactory<Program>
{
    public const string FakeConnectionString =
        "User Id=LOCAL_ONLY;Password=LOCAL_ONLY_NOT_A_SECRET;Data Source=127.0.0.1:1/LOCAL_ONLY;Connection Timeout=1";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Oracle:ConnectionString"] = FakeConnectionString
            });
        });
    }
}