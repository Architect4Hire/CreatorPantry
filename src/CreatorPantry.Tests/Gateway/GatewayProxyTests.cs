extern alias ApiService;
extern alias Gateway;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Modules.Auth;
using CreatorPantry.Domain.Modules.Auth.Managers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Forwarder;

namespace CreatorPantry.Tests.Gateway;

/// <summary>
/// The real gateway (YARP config, transforms, service discovery) in front of an in-memory downstream: either
/// the real ApiService or a server that echoes what it received.
/// </summary>
public sealed class GatewayProxyTests : IAsyncLifetime
{
    private WebApplication _echo = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _echo = builder.Build();
        _echo.Map("/{**path}", (HttpRequest request) => Results.Json(new EchoedRequest(
            request.Path + request.QueryString,
            request.Headers.ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase))));
        await _echo.StartAsync();
    }

    public async ValueTask DisposeAsync() => await _echo.DisposeAsync();

    [Fact]
    public async Task Anonymous_health_request_traverses_the_gateway_to_the_api()
    {
        using var api = new WebApplicationFactory<ApiService::Program>().WithWebHostBuilder(TestDatabase.ConfigureWithoutHealthCheck);
        using var gateway = CreateGateway(api.Server.CreateHandler);
        using var client = gateway.CreateClient();

        var response = await client.GetAsync("/api/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Api_routes_reach_the_api_with_their_path_unchanged()
    {
        using var api = new WebApplicationFactory<ApiService::Program>().WithWebHostBuilder(TestDatabase.ConfigureWithoutHealthCheck);
        using var gateway = CreateGateway(api.Server.CreateHandler);
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);

        // Unsafe requests need the antiforgery token the SPA obtains from the gateway.
        var antiforgery = await client.GetFromJsonAsync<JsonElement>("/bff/antiforgery", TestContext.Current.CancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register") { Content = JsonContent.Create(new { email = "bad" }) };
        request.Headers.Add("X-XSRF-TOKEN", antiforgery.GetProperty("requestToken").GetString());

        // Fails validation inside the API, so no database is needed; the stable code proves the API answered.
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(AuthErrorCodes.RegistrationInvalid, body.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("/api/v1/recipes/42?view=full")]
    [InlineData("/api/v2/recipes/42")] // versions the API does not support yet are still passed through intact
    [InlineData("/api/auth/register")] // unversioned: the API, not the gateway, decides it does not exist
    public async Task Catch_all_preserves_versioned_paths_and_query(string path)
    {
        var echoed = await SendThroughGatewayAsync(new HttpRequestMessage(HttpMethod.Get, path));

        Assert.Equal(path, echoed.Path);
    }

    [Fact]
    public async Task Health_route_is_rewritten_to_the_api_health_path()
    {
        var echoed = await SendThroughGatewayAsync(new HttpRequestMessage(HttpMethod.Get, "/api/health"));

        Assert.Equal("/health", echoed.Path);
    }

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/api/v1/anything")]
    public async Task Client_credentials_are_stripped_and_forwarding_headers_replaced(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer client-supplied");
        request.Headers.TryAddWithoutValidation("Cookie", "session=client-supplied");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.66");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "attacker.example");

        var echoed = await SendThroughGatewayAsync(request);

        Assert.False(echoed.Headers.ContainsKey("Authorization"));
        Assert.False(echoed.Headers.ContainsKey("Cookie"));
        Assert.DoesNotContain("203.0.113.66", echoed.Headers.GetValueOrDefault("X-Forwarded-For") ?? string.Empty);
        Assert.NotEqual("attacker.example", echoed.Headers.GetValueOrDefault("X-Forwarded-Host"));
        Assert.True(echoed.Headers.ContainsKey("X-Forwarded-Proto"));
    }

    [Theory]
    [InlineData("/health")] // the gateway's own endpoint
    [InlineData("/apix/health")]
    [InlineData("/other")]
    public async Task Paths_outside_api_are_not_proxied(string path)
    {
        var counter = new CountingHandler(_echo.GetTestServer().CreateHandler());
        using var gateway = CreateGateway(() => counter);
        using var client = gateway.CreateClient();

        await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(0, counter.Requests);
    }

    [Fact]
    public async Task Api_paths_are_proxied_exactly_once()
    {
        var counter = new CountingHandler(_echo.GetTestServer().CreateHandler());
        using var gateway = CreateGateway(() => counter);
        using var client = gateway.CreateClient();

        await client.GetAsync("/api/v1/anything", TestContext.Current.CancellationToken);

        Assert.Equal(1, counter.Requests);
    }

    private async Task<EchoedRequest> SendThroughGatewayAsync(HttpRequestMessage request)
    {
        using var gateway = CreateGateway(() => _echo.GetTestServer().CreateHandler());
        using var client = gateway.CreateClient();

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EchoedRequest>(TestContext.Current.CancellationToken))!;
    }

    /// <summary>The real gateway, with YARP's forwarder sending to the supplied in-memory handler.</summary>
    internal static WebApplicationFactory<Gateway::Program> CreateGateway(
        Func<HttpMessageHandler> downstream, System.Security.Claims.ClaimsPrincipal? sessionUser = null) =>
        GatewayTestHost.Create(proxyDownstream: downstream, sessionUser: sessionUser);

    private sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private int _requests;

        public int Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return base.SendAsync(request, cancellationToken);
        }
    }

    internal sealed record EchoedRequest(string Path, Dictionary<string, string> Headers);
}
