extern alias ApiService;
extern alias Gateway;
extern alias Web;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CreatorPantry.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Gateway;

/// <summary>Prompt 1.9a: security response headers, request-body limits, and trusted-proxy handling.</summary>
public sealed class EdgeHardeningTests : IAsyncLifetime
{
    private WebApplication _echo = null!;
    private int _downstreamRequests;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _echo = builder.Build();
        _echo.Map("/{**path}", (HttpRequest request, HttpResponse response) =>
        {
            Interlocked.Increment(ref _downstreamRequests);
            // A downstream trying to loosen browser policy must not win.
            response.Headers.ContentSecurityPolicy = "default-src *";
            response.Headers["Server"] = "Kestrel";
            return Results.Json(new GatewayProxyTests.EchoedRequest(
                request.Path.Value!,
                request.Headers.ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase)));
        });
        await _echo.StartAsync();
    }

    public async ValueTask DisposeAsync() => await _echo.DisposeAsync();

    // ---- Security response headers --------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/bff/session")] // gateway endpoint
    [InlineData("GET", "/api/v1/anything")] // proxied response
    [InlineData("GET", "/health")] // probe
    [InlineData("GET", "/nothing-here")] // 404
    [InlineData("POST", "/api/v1/anything")] // 400 csrf.invalid
    public async Task Every_gateway_response_carries_the_security_headers(string method, string path)
    {
        using var gateway = CreateGateway();
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), TestContext.Current.CancellationToken);

        AssertBaselineHeaders(response);
        Assert.Equal(EdgeHardening.ApiContentSecurityPolicy, Header(response, "Content-Security-Policy"));
        Assert.False(response.Headers.Contains("Strict-Transport-Security"), "HSTS must not be sent in Development.");
    }

    [Fact]
    public async Task Hsts_is_sent_outside_development()
    {
        using var gateway = GatewayTestHost.Create(
            proxyDownstream: () => _echo.GetTestServer().CreateHandler(),
            configure: web => web.UseEnvironment("Production"));
        using var client = gateway.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://gateway.creatorpantry.test") });

        var response = await client.GetAsync("/bff/session", TestContext.Current.CancellationToken);

        Assert.StartsWith("max-age=", Header(response, "Strict-Transport-Security"));
    }

    [Fact]
    public async Task Web_host_serves_the_spa_with_a_strict_script_policy_and_gateway_only_connections()
    {
        var webRoot = Directory.CreateTempSubdirectory("cp-webroot-").FullName;
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<!doctype html><cp-root></cp-root>", TestContext.Current.CancellationToken);
        try
        {
            using var web = new WebApplicationFactory<Web::Program>().WithWebHostBuilder(host =>
            {
                host.UseWebRoot(webRoot);
                host.UseSetting("Client:GatewayUrl", "https://gateway.creatorpantry.test/");
            });
            using var client = web.CreateClient();

            var index = await client.GetAsync("/recipes/42", TestContext.Current.CancellationToken);
            var config = await client.GetAsync("/runtime-config.json", TestContext.Current.CancellationToken);

            foreach (var response in new[] { index, config })
            {
                AssertBaselineHeaders(response);
                var csp = Header(response, "Content-Security-Policy");
                Assert.Contains("script-src 'self';", csp);
                Assert.Contains("connect-src 'self' https://gateway.creatorpantry.test;", csp);
                Assert.Contains("frame-ancestors 'none'", csp);
                Assert.Contains("object-src 'none'", csp);
                Assert.DoesNotContain("unsafe-eval", csp);
                Assert.DoesNotContain("*", csp);
            }
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    // ---- Request-body limits --------------------------------------------------------------------------

    [Fact]
    public async Task Gateway_rejects_bodies_over_the_default_four_megabytes_before_forwarding()
    {
        using var gateway = CreateGateway();
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);
        var before = _downstreamRequests;

        var response = await PostBytesAsync(client, "/api/v1/anything", EdgeHardening.DefaultMaxRequestBodyBytes + 1);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(EdgeHardening.RequestTooLargeCode, (await ProblemAsync(response)).GetProperty("code").GetString());
        Assert.Equal(before, _downstreamRequests);
    }

    [Fact]
    public async Task Body_limit_is_configurable_and_allows_bodies_within_it()
    {
        using var gateway = CreateGateway(configure: web => web.UseSetting(EdgeHardening.MaxRequestBodyConfigurationKey, "1000"));
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);
        var token = await AntiforgeryTokenAsync(client);

        var within = await PostBytesAsync(client, "/api/v1/anything", 900, token);
        var over = await PostBytesAsync(client, "/api/v1/anything", 1001, token);

        Assert.Equal(HttpStatusCode.OK, within.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
    }

    [Fact]
    public async Task Api_rejects_bodies_over_its_limit_with_the_stable_code()
    {
        using var api = new WebApplicationFactory<ApiService::Program>().WithWebHostBuilder(TestDatabase.ConfigureWithoutHealthCheck);
        using var client = api.CreateClient();

        var response = await PostBytesAsync(client, "/api/v1/auth/register", EdgeHardening.DefaultMaxRequestBodyBytes + 1);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal(EdgeHardening.RequestTooLargeCode, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Endpoints_can_raise_their_own_limit_for_uploads()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddProblemDetails();
        await using var app = builder.Build();
        app.UseRequestBodyLimit();
        app.MapPost("/small", () => Results.Ok());
        app.MapPost("/upload", () => Results.Ok()).WithMetadata(new RequestSizeLimitAttribute(8 * 1024 * 1024));
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();

        var small = await PostBytesAsync(client, "/small", EdgeHardening.DefaultMaxRequestBodyBytes + 1);
        var upload = await PostBytesAsync(client, "/upload", EdgeHardening.DefaultMaxRequestBodyBytes + 1);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, small.StatusCode);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
    }

    // ---- Trusted proxies ------------------------------------------------------------------------------

    [Fact]
    public async Task Without_trusted_proxies_a_forged_client_address_is_ignored()
    {
        var echoed = await ProxyAsync(configure: null, connection: "203.0.113.5", forwardedFor: "198.51.100.7");

        Assert.Equal("203.0.113.5", echoed.Headers["X-Forwarded-For"]);
    }

    [Fact]
    public async Task A_trusted_proxy_supplies_the_real_client_address_and_scheme()
    {
        var echoed = await ProxyAsync(
            configure: web => web.UseSetting("Edge:TrustedProxies:0", "10.0.0.4"),
            connection: "10.0.0.4", forwardedFor: "198.51.100.7", forwardedProto: "https");

        Assert.Equal("198.51.100.7", echoed.Headers["X-Forwarded-For"]);
        Assert.Equal("https", echoed.Headers["X-Forwarded-Proto"]);
    }

    [Fact]
    public async Task A_trusted_network_is_honored_but_other_senders_are_not()
    {
        void Configure(IWebHostBuilder web) => web.UseSetting("Edge:TrustedNetworks:0", "10.0.0.0/16");

        var fromNetwork = await ProxyAsync(Configure, connection: "10.0.200.9", forwardedFor: "198.51.100.7");
        var fromOutside = await ProxyAsync(Configure, connection: "203.0.113.5", forwardedFor: "198.51.100.7");

        Assert.Equal("198.51.100.7", fromNetwork.Headers["X-Forwarded-For"]);
        Assert.Equal("203.0.113.5", fromOutside.Headers["X-Forwarded-For"]);
    }

    [Fact]
    public async Task Only_one_hop_is_trusted_so_a_client_cannot_prepend_addresses()
    {
        var echoed = await ProxyAsync(
            configure: web => web.UseSetting("Edge:TrustedProxies:0", "10.0.0.4"),
            connection: "10.0.0.4", forwardedFor: "192.0.2.66, 198.51.100.7");

        Assert.Equal("198.51.100.7", echoed.Headers["X-Forwarded-For"]);
    }

    [Theory]
    [InlineData("Edge:TrustedProxies:0", "not-an-address")]
    [InlineData("Edge:TrustedNetworks:0", "10.0.0.0/99")]
    public void Invalid_proxy_configuration_stops_startup(string key, string value)
    {
        using var gateway = CreateGateway(configure: web => web.UseSetting(key, value));

        var error = Assert.Throws<InvalidOperationException>(() => gateway.CreateClient());

        Assert.Contains(key.Split(':')[0] + ":" + key.Split(':')[1], error.Message);
    }

    [Fact]
    public async Task Sign_in_limit_uses_the_real_client_so_clients_behind_one_proxy_are_limited_separately()
    {
        using var gateway = CreateGateway(configure: web => web.UseSetting("Edge:TrustedProxies:0", "10.0.0.4"));
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await LoginThroughProxyAsync(client, "198.51.100.7");
        }

        var clientAAgain = await LoginThroughProxyAsync(client, "198.51.100.7");
        var clientB = await LoginThroughProxyAsync(client, "198.51.100.8");

        Assert.Equal(HttpStatusCode.TooManyRequests, clientAAgain.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, clientB.StatusCode);
    }

    [Fact]
    public async Task Forged_client_addresses_cannot_escape_the_sign_in_limit()
    {
        using var gateway = CreateGateway(); // no trusted proxies
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);

        HttpResponseMessage last = null!;
        for (var attempt = 0; attempt < 11; attempt++)
        {
            last = await LoginThroughProxyAsync(client, $"198.51.100.{attempt + 1}", connection: "203.0.113.5");
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------

    private WebApplicationFactory<Gateway::Program> CreateGateway(Action<IWebHostBuilder>? configure = null) =>
        GatewayTestHost.Create(
            proxyDownstream: () => _echo.GetTestServer().CreateHandler(),
            apiSessions: () => new SignInRejectedHandler(),
            configure: configure);

    private async Task<GatewayProxyTests.EchoedRequest> ProxyAsync(
        Action<IWebHostBuilder>? configure, string connection, string forwardedFor, string? forwardedProto = null)
    {
        using var gateway = CreateGateway(configure);
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/anything");
        request.Headers.Add(GatewayTestHost.ConnectionAddressHeader, connection);
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (forwardedProto is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<GatewayProxyTests.EchoedRequest>(TestContext.Current.CancellationToken))!;
    }

    private static async Task<HttpResponseMessage> LoginThroughProxyAsync(HttpClient client, string clientAddress, string connection = "10.0.0.4")
    {
        var token = await AntiforgeryTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/login")
        {
            Content = JsonContent.Create(new { email = "cook@example.com", password = "wrong" }),
        };
        request.Headers.Add("X-XSRF-TOKEN", token);
        request.Headers.Add(GatewayTestHost.ConnectionAddressHeader, connection);
        request.Headers.Add("X-Forwarded-For", clientAddress);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string> AntiforgeryTokenAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/bff/antiforgery", TestContext.Current.CancellationToken))
            .GetProperty("requestToken").GetString()!;

    private static async Task<HttpResponseMessage> PostBytesAsync(HttpClient client, string path, long length, string? antiforgery = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(new byte[length]) { Headers = { ContentType = new("application/json") } },
        };
        if (antiforgery is not null)
        {
            request.Headers.Add("X-XSRF-TOKEN", antiforgery);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static void AssertBaselineHeaders(HttpResponseMessage response)
    {
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
        Assert.Equal("same-origin", Header(response, "Cross-Origin-Opener-Policy"));
        Assert.Contains("camera=()", Header(response, "Permissions-Policy"));
        Assert.False(response.Headers.Contains("Server"), "The Server header must not be sent.");
        Assert.False(response.Headers.Contains("X-Powered-By"));
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values)
        : response.Content.Headers.TryGetValues(name, out var contentValues) ? string.Join(",", contentValues)
        : throw new Xunit.Sdk.XunitException($"Missing response header {name}.");

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    /// <summary>Stands in for the API's credential check: every sign-in is rejected.</summary>
    private sealed class SignInRejectedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"code":"auth.signin.failed","title":"Sign-in failed."}""", Encoding.UTF8, "application/problem+json"),
            });
    }
}
