using System.Net;
using System.Net.Http.Json;

namespace CreatorPantry.Tests.Gateway;

/// <summary>
/// The ops routes are never reachable through the browser edge (USAGE-009, baseline B-14).
/// </summary>
/// <remarks>
/// The proxy already strips <c>Authorization</c> on every hop, so an ops key could not survive the journey
/// even if a path were forwarded. These tests hold the stronger property: the gateway does not forward them at
/// all, so a browser session cannot reach a machine route and the routes' existence is not disclosed either.
/// </remarks>
public sealed class OpsRouteBlockingTests
{
    [Theory]
    [InlineData("/api/v1/ops/ai-usage/accounts/user-sam")]
    [InlineData("/api/v1/ops/ai-usage/top-consumers")]

    // Case and duplicated slashes reach the API as the same request, so they must be the same answer here.
    [InlineData("/API/V1/OPS/ai-usage/accounts/user-sam")]
    [InlineData("//api//v1//ops//ai-usage/accounts/user-sam")]
    [InlineData("/api/v1/ops")]
    public async Task Ops_routes_are_not_reachable_through_the_gateway(string path)
    {
        await using var api = await SqliteApiHost.StartAsync();
        var forwarded = 0;
        using var gateway = GatewayTestHost.Create(
            proxyDownstream: () => new CountingHandler(api.Factory.Server.CreateHandler(), () => forwarded++));
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, forwarded);
    }

    /// <summary>A command is refused at the edge as firmly as a read, and reaches nothing behind it.</summary>
    [Theory]
    [InlineData("/api/v1/ops/ai-usage/accounts/user-sam/quota/clear")]
    [InlineData("/api/v1/ops/ai-usage/accounts/user-sam/ai-access/suspend")]
    public async Task Ops_commands_are_not_reachable_through_the_gateway(string path)
    {
        await using var api = await SqliteApiHost.StartAsync();
        var forwarded = 0;
        using var gateway = GatewayTestHost.Create(
            proxyDownstream: () => new CountingHandler(api.Factory.Server.CreateHandler(), () => forwarded++));
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);

        var response = await client.PostAsync(
            path, JsonContent.Create(new { reason = "Trying it on." }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, forwarded);
    }

    /// <summary>
    /// Blocking <c>ops</c> did not widen into the product surface. A path that merely contains the letters is
    /// an ordinary route and is still proxied — the regex matches a whole segment after the version.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/operations/abc")]
    [InlineData("/api/v1/workspaces/ops/recipes")]
    public async Task Ordinary_routes_that_merely_contain_ops_are_still_proxied(string path)
    {
        await using var api = await SqliteApiHost.StartAsync();
        var forwarded = 0;
        using var gateway = GatewayTestHost.Create(
            proxyDownstream: () => new CountingHandler(api.Factory.Server.CreateHandler(), () => forwarded++));
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);

        await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(1, forwarded);
    }

    /// <summary>Counts what actually reached the API, so "refused" is distinguishable from "forwarded and 404".</summary>
    private sealed class CountingHandler(HttpMessageHandler inner, Action onSend) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onSend();

            return base.SendAsync(request, cancellationToken);
        }
    }
}
