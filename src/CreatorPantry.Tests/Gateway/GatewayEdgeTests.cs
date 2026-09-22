extern alias Gateway;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using CreatorPantry.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace CreatorPantry.Tests.Gateway;

/// <summary>Edge policy: header trust, exact-origin CORS, and antiforgery on unsafe proxied requests.</summary>
public sealed class GatewayEdgeTests : IAsyncLifetime
{
    private const string AllowedOrigin = "https://app.creatorpantry.test";

    private WebApplication _echo = null!;
    private int _downstreamRequests;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _echo = builder.Build();
        _echo.Map("/{**path}", (HttpRequest request) =>
        {
            Interlocked.Increment(ref _downstreamRequests);
            return Results.Json(new GatewayProxyTests.EchoedRequest(
                request.Path.Value!,
                request.Headers.ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase)));
        });
        await _echo.StartAsync();
    }

    public async ValueTask DisposeAsync() => await _echo.DisposeAsync();

    // ---- Forged headers -------------------------------------------------------------------------------

    public static TheoryData<string, string> ForgedHeaders => new()
    {
        { "Authorization", "Bearer eyJhbGciOiJub25lIn0.eyJzdWIiOiJhZG1pbiJ9." },
        { "Proxy-Authorization", "Basic YWRtaW46YWRtaW4=" },
        { "Cookie", "__Host-creatorpantry-session=forged" },
        { "Forwarded", "for=203.0.113.9;proto=https;host=attacker.example" },
        { "X-Forwarded-User", "admin" },
        { "X-Forwarded-Email", "admin@example.com" },
        { "X-Forwarded-Prefix", "/admin" },
        { "X-Real-IP", "203.0.113.9" },
        { "X-Client-IP", "203.0.113.9" },
        { "True-Client-IP", "203.0.113.9" },
        { "X-MS-CLIENT-PRINCIPAL", "eyJ1c2VySWQiOiJhZG1pbiJ9" },
        { "X-MS-CLIENT-PRINCIPAL-ID", "admin" },
        { "X-User-Id", "admin" },
        { "X-Workspace-Id", "00000000-0000-0000-0000-000000000001" },
        { "X-Internal-Token", "forged" },
        { "X-XSRF-TOKEN", "consumed-at-the-edge" },
        { "baggage", "userId=admin" },
    };

    [Theory]
    [MemberData(nameof(ForgedHeaders))]
    public async Task Caller_identity_and_forwarding_headers_never_reach_the_api(string name, string value)
    {
        var echoed = await SendAsync(HttpMethod.Get, "/api/v1/anything", request => request.Headers.TryAddWithoutValidation(name, value));

        Assert.DoesNotContain(echoed.Headers, header => header.Value.Contains(value, StringComparison.Ordinal));
        if (!name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) || name == "X-Forwarded-User"
            || name == "X-Forwarded-Email" || name == "X-Forwarded-Prefix")
        {
            Assert.False(echoed.Headers.ContainsKey(name), $"{name} was forwarded.");
        }
    }

    [Fact]
    public async Task Forged_forwarding_values_are_replaced_by_the_gateways_own()
    {
        var echoed = await SendAsync(HttpMethod.Get, "/api/v1/anything", request =>
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.9");
            request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "attacker.example");
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "gopher");
        });

        Assert.DoesNotContain("203.0.113.9", echoed.Headers.GetValueOrDefault("X-Forwarded-For") ?? string.Empty);
        Assert.Equal("localhost", echoed.Headers["X-Forwarded-Host"]);
        Assert.Equal("https", echoed.Headers["X-Forwarded-Proto"]);
    }

    [Fact]
    public async Task Allowlisted_caller_headers_are_forwarded()
    {
        var echoed = await SendAsync(HttpMethod.Get, "/api/v1/anything", request =>
        {
            request.Headers.TryAddWithoutValidation("Accept-Language", "fr-CA");
            request.Headers.TryAddWithoutValidation("Idempotency-Key", "cmd-123");
            request.Headers.TryAddWithoutValidation("If-Match", "\"v7\"");
        });

        Assert.Equal("fr-CA", echoed.Headers["Accept-Language"]);
        Assert.Equal("cmd-123", echoed.Headers["Idempotency-Key"]);
        Assert.Equal("\"v7\"", echoed.Headers["If-Match"]);
    }

    [Fact]
    public async Task Signed_in_request_carries_only_the_gateway_token_even_when_the_caller_forges_one()
    {
        var sessionUser = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "user-1"),
            new Claim(InternalTokenDefaults.SessionIdClaim, "session-1"),
        ], "bff-session"));

        var echoed = await SendAsync(HttpMethod.Get, "/api/v1/anything",
            request => request.Headers.TryAddWithoutValidation("Authorization", "Bearer forged"), sessionUser);

        var authorization = echoed.Headers["Authorization"];
        Assert.DoesNotContain("forged", authorization);
        Assert.Equal("user-1", new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(authorization["Bearer ".Length..]).Subject);
    }

    // ---- CORS -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Preflight_from_the_configured_origin_allows_credentials_for_that_exact_origin()
    {
        var response = await PreflightAsync(AllowedOrigin);

        Assert.Equal(AllowedOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single());
        Assert.Contains("X-XSRF-TOKEN", string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("https://app.creatorpantry.test.evil.example")]
    [InlineData("http://app.creatorpantry.test")] // scheme differs
    [InlineData("https://app.creatorpantry.test:8443")] // port differs
    [InlineData("null")]
    public async Task Other_origins_receive_no_cors_grant(string origin)
    {
        var preflight = await PreflightAsync(origin);

        using var gateway = CreateGateway();
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);
        using var simple = new HttpRequestMessage(HttpMethod.Get, "/bff/session");
        simple.Headers.Add("Origin", origin);
        var actual = await client.SendAsync(simple, TestContext.Current.CancellationToken);

        Assert.False(preflight.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(actual.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(actual.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.creatorpantry.test")]
    [InlineData("https://app.creatorpantry.test/path")]
    [InlineData("https://app.creatorpantry.test?x=1")]
    [InlineData("app.creatorpantry.test")]
    [InlineData("ftp://app.creatorpantry.test")]
    public void Gateway_refuses_to_start_with_a_wildcard_or_non_origin_entry(string origin)
    {
        using var gateway = GatewayTestHost.Create(configure: web => web.UseSetting("Cors:AllowedOrigins:0", origin));

        var error = Assert.Throws<InvalidOperationException>(() => gateway.CreateClient());

        Assert.Contains("Cors:AllowedOrigins", error.Message);
    }

    // ---- Antiforgery ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Unsafe_proxied_requests_without_antiforgery_are_rejected_before_the_api(string method)
    {
        using var gateway = CreateGateway();
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);
        var before = _downstreamRequests;

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/api/v1/anything")
        {
            Content = JsonContent.Create(new { }),
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("csrf.invalid",
            (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("code").GetString());
        Assert.Equal(before, _downstreamRequests);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Unsafe_proxied_requests_with_a_valid_token_reach_the_api(string method)
    {
        using var gateway = CreateGateway();
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);
        var token = (await client.GetFromJsonAsync<JsonElement>("/bff/antiforgery", TestContext.Current.CancellationToken))
            .GetProperty("requestToken").GetString();

        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/v1/anything") { Content = JsonContent.Create(new { }) };
        request.Headers.Add("X-XSRF-TOKEN", token);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/api/v1/anything",
            (await response.Content.ReadFromJsonAsync<GatewayProxyTests.EchoedRequest>(TestContext.Current.CancellationToken))!.Path);
    }

    [Theory]
    [InlineData("/api/v1/anything")]
    [InlineData("/api/health")]
    [InlineData("/health")]
    [InlineData("/alive")]
    [InlineData("/bff/session")]
    public async Task Safe_requests_and_health_need_no_antiforgery_token(string path)
    {
        using var gateway = CreateGateway();
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------

    private WebApplicationFactory<Gateway::Program> CreateGateway(ClaimsPrincipal? sessionUser = null) =>
        GatewayTestHost.Create(
            proxyDownstream: () => _echo.GetTestServer().CreateHandler(),
            sessionUser: sessionUser,
            configure: web => web.UseSetting("Cors:AllowedOrigins:0", AllowedOrigin));

    private async Task<GatewayProxyTests.EchoedRequest> SendAsync(
        HttpMethod method, string path, Action<HttpRequestMessage> configure, ClaimsPrincipal? sessionUser = null)
    {
        using var gateway = CreateGateway(sessionUser);
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);
        using var request = new HttpRequestMessage(method, path);
        configure(request);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<GatewayProxyTests.EchoedRequest>(TestContext.Current.CancellationToken))!;
    }

    private async Task<HttpResponseMessage> PreflightAsync(string origin)
    {
        using var gateway = CreateGateway();
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/anything");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-xsrf-token");

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
