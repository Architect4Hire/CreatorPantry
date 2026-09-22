extern alias ApiService;
extern alias Gateway;
extern alias Web;

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CreatorPantry.Tests.Health;

public class HealthEndpointTests
{
    public static TheoryData<string> Hosts => ["ApiService", "Gateway", "Web"];

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Health_and_alive_return_healthy(string host)
    {
        using var client = CreateClient(host, failingDependency: false);

        var health = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        var alive = await client.GetAsync("/alive", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Alive_ignores_checks_not_tagged_live(string host)
    {
        using var client = CreateClient(host, failingDependency: true);

        var health = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        var alive = await client.GetAsync("/alive", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
    }

    private static HttpClient CreateClient(string host, bool failingDependency) => host switch
    {
        "ApiService" => Configure(
            new WebApplicationFactory<ApiService::Program>().WithWebHostBuilder(TestDatabase.ConfigureWithoutHealthCheck),
            failingDependency).CreateClient(),
        "Gateway" => Configure(Gateway.GatewayTestHost.Create(), failingDependency).CreateClient(),
        "Web" => Configure(
            new WebApplicationFactory<Web::Program>()
                .WithWebHostBuilder(web => web.UseSetting("Client:GatewayUrl", "https://gateway.test")),
            failingDependency).CreateClient(),
        _ => throw new ArgumentOutOfRangeException(nameof(host), host, null),
    };

    // A failing untagged check stands in for a future dependency probe (SQL, Redis, blob).
    private static WebApplicationFactory<T> Configure<T>(WebApplicationFactory<T> factory, bool failingDependency)
        where T : class =>
        !failingDependency
            ? factory
            : factory.WithWebHostBuilder(web => web.ConfigureServices(services =>
                services.AddHealthChecks().AddCheck("dependency", () => HealthCheckResult.Unhealthy())));
}
