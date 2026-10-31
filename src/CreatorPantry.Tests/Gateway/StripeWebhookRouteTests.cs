extern alias Gateway;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CreatorPantry.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace CreatorPantry.Tests.Gateway;

/// <summary>
/// The payment provider's webhook route at the edge (AP.6.2): reachable by Stripe, which is not a browser, and by
/// nothing else as more than an anonymous POST — no antiforgery token asked, no browser session carried, its
/// signature forwarded on it alone, its own body limit and its own rate limit.
/// </summary>
/// <remarks>
/// Against the real gateway with an echoing downstream, so each assertion is about what the API would receive. That
/// the API then verifies the signature is <c>BillingWebhookEndpointTests</c>.
/// </remarks>
public sealed class StripeWebhookRouteTests : IAsyncLifetime
{
    private const string Route = "/api/v1/webhooks/stripe";

    private const string Signature = "t=2084256000,v1=" + "ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12";

    private WebApplication _echo = null!;

    private int _downstreamRequests;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _echo = builder.Build();
        _echo.Map("/{**path}", (HttpRequest request) =>
        {
            Interlocked.Increment(ref _downstreamRequests);
            return Results.Json(new GatewayProxyTests.EchoedRequest(
                request.Path + request.QueryString,
                request.Headers.ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase)));
        });
        await _echo.StartAsync();
    }

    public async ValueTask DisposeAsync() => await _echo.DisposeAsync();

    /// <summary>Stripe has no antiforgery token and never will: the route is the one unsafe API request exempt.</summary>
    [Fact]
    public async Task A_delivery_reaches_the_api_with_no_antiforgery_token_and_its_signature_intact()
    {
        var echoed = await DeliverAsync(Webhook());

        Assert.Equal(Route, echoed.Path);
        Assert.Equal(Signature, echoed.Headers["Stripe-Signature"]);
    }

    /// <summary>
    /// A browser pointed at the route brings its cookie; the gateway authenticates it as it does everywhere, and must
    /// still send the API no token for it — the route takes no browser session.
    /// </summary>
    [Fact]
    public async Task A_delivery_that_arrives_with_a_browser_session_carries_no_credential_to_the_api()
    {
        var sessionUser = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "user-1"),
            new Claim(InternalTokenDefaults.SessionIdClaim, "session-1"),
        ], "bff-session"));

        var request = Webhook();
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer forged");
        request.Headers.TryAddWithoutValidation("Cookie", "cp-session=whatever");

        var echoed = await DeliverAsync(request, sessionUser);

        Assert.False(echoed.Headers.ContainsKey("Authorization"));
        Assert.False(echoed.Headers.ContainsKey("Cookie"));
    }

    /// <summary>
    /// The same session on any other API route does get its token, so the test above is about the route rather than
    /// about a gateway that attaches nothing.
    /// </summary>
    [Fact]
    public async Task The_same_session_on_another_route_is_still_forwarded_as_its_user()
    {
        var sessionUser = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "user-1"),
            new Claim(InternalTokenDefaults.SessionIdClaim, "session-1"),
        ], "bff-session"));

        var echoed = await DeliverAsync(new HttpRequestMessage(HttpMethod.Get, "/api/v1/anything"), sessionUser);

        Assert.StartsWith("Bearer ", echoed.Headers["Authorization"], StringComparison.Ordinal);
    }

    /// <summary>The signature header is input the API trusts, so it reaches the API on this route and no other.</summary>
    [Theory]
    [InlineData("GET", "/api/v1/anything")]
    [InlineData("GET", "/api/v1/plans")]
    public async Task The_signature_header_is_stripped_everywhere_else(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.TryAddWithoutValidation("Stripe-Signature", Signature);

        var echoed = await DeliverAsync(request);

        Assert.False(echoed.Headers.ContainsKey("Stripe-Signature"));
    }

    /// <summary>
    /// The exemption is the exact path and method. A spelling the route itself would match still answers to the
    /// antiforgery check, so no variant becomes an unprotected way in.
    /// </summary>
    [Theory]
    [InlineData("POST", "/api/v1/webhooks/STRIPE")]
    [InlineData("POST", "/api/v1/webhooks/stripe/")]
    [InlineData("PUT", "/api/v1/webhooks/stripe")]
    public async Task Only_the_exact_route_is_exempt_from_antiforgery(string method, string path)
    {
        using var gateway = CreateGateway();
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = Json() };

        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("csrf.invalid", (await ProblemAsync(response)).GetProperty("code").GetString());
        Assert.Equal(0, _downstreamRequests);
    }

    /// <summary>The route's own body limit, the API action's number (UploadRouteLimitDriftTests holds them together).</summary>
    [Fact]
    public async Task A_body_over_the_routes_limit_is_refused_at_the_edge()
    {
        using var gateway = CreateGateway();
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);
        using var request = Webhook(new string('x', (512 * 1024) + 1));

        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, _downstreamRequests);
    }

    /// <summary>
    /// Its own allowance, per address: generous, and bounded. The 601st delivery from one address inside the minute is
    /// refused at the edge, and another address is unaffected.
    /// </summary>
    [Fact]
    public async Task The_route_has_its_own_rate_limit_per_address()
    {
        using var gateway = CreateGateway();
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);

        for (var delivery = 0; delivery < 600; delivery++)
        {
            using var accepted = await client.SendAsync(Webhook(from: "198.51.100.7"), Ct);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        using var refused = await client.SendAsync(Webhook(from: "198.51.100.7"), Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(refused.Headers.Contains("Retry-After"));

        using var other = await client.SendAsync(Webhook(from: "198.51.100.8"), Ct);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static StringContent Json(string body = "{\"id\":\"evt_1\"}") => new(body, Encoding.UTF8, "application/json");

    private static HttpRequestMessage Webhook(string? body = null, string? from = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = Json(body ?? "{\"id\":\"evt_1\"}") };
        request.Headers.TryAddWithoutValidation("Stripe-Signature", Signature);
        if (from is not null)
        {
            request.Headers.TryAddWithoutValidation(GatewayTestHost.ConnectionAddressHeader, from);
        }

        return request;
    }

    private WebApplicationFactory<Gateway::Program> CreateGateway(ClaimsPrincipal? sessionUser = null) =>
        GatewayTestHost.Create(proxyDownstream: () => _echo.GetTestServer().CreateHandler(), sessionUser: sessionUser);

    private async Task<GatewayProxyTests.EchoedRequest> DeliverAsync(HttpRequestMessage request, ClaimsPrincipal? sessionUser = null)
    {
        using var gateway = CreateGateway(sessionUser);
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);

        using (request)
        {
            var response = await client.SendAsync(request, Ct);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<GatewayProxyTests.EchoedRequest>(Ct))!;
        }
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
}
