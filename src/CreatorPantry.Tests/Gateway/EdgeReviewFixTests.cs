extern alias ApiService;
extern alias Gateway;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ApiService::CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CreatorPantry.Tests.Gateway;

/// <summary>Regression tests for the prompt 1.10 reviewer findings.</summary>
public sealed class EdgeReviewFixTests
{
    private const string AllowedOrigin = "https://app.creatorpantry.test";

    // ---- Development tooling is never proxied ---------------------------------------------------------

    [Theory]
    [InlineData("/api/dev/account-messages")]
    [InlineData("/API/DEV/account-messages")]
    [InlineData("//api//dev/account-messages")]
    [InlineData("/_dev/account-messages")]
    public async Task Development_reset_token_dump_is_not_reachable_through_the_gateway(string path)
    {
        await using var api = await SqliteApiHost.StartAsync();
        var forwarded = 0;
        using var gateway = GatewayTestHost.Create(proxyDownstream: () => new CountingHandler(api.Factory.Server.CreateHandler(), () => forwarded++));
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, forwarded);
    }

    // ---- Every gateway failure is ProblemDetails --------------------------------------------------------

    [Theory]
    [InlineData("POST", "/bff/login", "{ not json", "bad_request", HttpStatusCode.BadRequest)]
    [InlineData("GET", "/bff/nothing-here", null, "not_found", HttpStatusCode.NotFound)]
    [InlineData("GET", "/bff/login", null, "method_not_allowed", HttpStatusCode.MethodNotAllowed)]
    public async Task Gateway_generated_failures_are_problem_details_with_code_and_trace(
        string method, string path, string? body, string code, HttpStatusCode status)
    {
        using var gateway = GatewayTestHost.Create(apiSessions: () => new StubApi());
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            request.Headers.Add("X-XSRF-TOKEN", await AntiforgeryAsync(client));
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, status, code);
    }

    [Fact]
    public async Task Api_outage_is_a_502_problem_rather_than_an_empty_response()
    {
        using var gateway = GatewayTestHost.Create(proxyDownstream: () => new ThrowingHandler());
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);

        var response = await client.GetAsync("/api/v1/anything", TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.BadGateway, "upstream_unavailable");
    }

    [Fact]
    public async Task Rejections_are_readable_by_the_cross_origin_spa()
    {
        using var gateway = GatewayTestHost.Create(configure: web => web.UseSetting("Cors:AllowedOrigins:0", AllowedOrigin));
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);
        using var oversized = new HttpRequestMessage(HttpMethod.Post, "/api/v1/anything")
        {
            Content = new ByteArrayContent(new byte[CreatorPantry.ServiceDefaults.EdgeHardening.DefaultMaxRequestBodyBytes + 1]),
        };
        oversized.Headers.Add("Origin", AllowedOrigin);

        var response = await client.SendAsync(oversized, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(AllowedOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Rate_limited_responses_say_when_to_retry()
    {
        using var gateway = GatewayTestHost.Create(apiSessions: () => new StubApi());
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);

        HttpResponseMessage last = null!;
        for (var attempt = 0; attempt < 11; attempt++)
        {
            last = await PostLoginAsync(client);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
        Assert.NotNull(last.Headers.RetryAfter);
    }

    // ---- Session bounds and revalidation ----------------------------------------------------------------

    [Theory]
    [InlineData("Session:RevalidationInterval", "09:00:00")] // longer than the idle window: revocation off
    [InlineData("Session:MaxValidationStaleness", "1.00:00:00")]
    [InlineData("Session:IdleTimeout", "00:00:00")]
    [InlineData("Session:AbsoluteLifetime", "60.00:00:00")]
    [InlineData("Session:RevalidationInterval", "-00:01:00")]
    public void Gateway_refuses_session_bounds_that_would_weaken_revocation(string key, string value)
    {
        using var gateway = GatewayTestHost.Create(configure: web => web.UseSetting(key, value));

        var error = Assert.Throws<OptionsValidationException>(() => gateway.CreateClient());

        Assert.Contains("RevalidationInterval", error.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Revalidation_fails_closed_when_the_api_refuses_the_check(HttpStatusCode refusal)
    {
        var api = new StubApi();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var gateway = GatewayWithClock(api, time);
        var client = HttpsClientWithCookies(gateway);
        Assert.Equal(HttpStatusCode.OK, (await PostLoginAsync(client)).StatusCode);

        api.ValidateResponse = () => new HttpResponseMessage(refusal);
        time.Advance(TimeSpan.FromMinutes(6));

        Assert.False(await IsSignedInAsync(client));
    }

    [Fact]
    public async Task Outage_keeps_the_session_only_until_the_staleness_bound()
    {
        var api = new StubApi();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var gateway = GatewayWithClock(api, time);
        var client = HttpsClientWithCookies(gateway);
        Assert.Equal(HttpStatusCode.OK, (await PostLoginAsync(client)).StatusCode);

        api.ValidateResponse = () => throw new HttpRequestException("API down");
        time.Advance(TimeSpan.FromMinutes(10));
        var duringShortOutage = await IsSignedInAsync(client);
        time.Advance(TimeSpan.FromMinutes(6)); // 16 minutes since the last successful validation
        var afterStaleness = await IsSignedInAsync(client);

        Assert.True(duringShortOutage);
        Assert.False(afterStaleness);
    }

    [Fact]
    public async Task Locking_out_an_account_ends_its_existing_sessions()
    {
        await using var api = await SqliteApiHost.StartAsync();
        var userId = await api.CreateUserAsync("cook@example.com", "correct horse battery");
        using var gateway = GatewayTestHost.Create(
            proxyDownstream: api.Factory.Server.CreateHandler,
            apiSessions: api.Factory.Server.CreateHandler,
            configure: web => web.UseSetting("Session:RevalidationInterval", "00:00:00"));
        var client = HttpsClientWithCookies(gateway);
        Assert.Equal(HttpStatusCode.OK, (await PostLoginAsync(client, "cook@example.com", "correct horse battery")).StatusCode);

        await using (var scope = api.Factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            await users.SetLockoutEndDateAsync((await users.FindByIdAsync(userId))!, DateTimeOffset.UtcNow.AddHours(1));
        }

        Assert.False(await IsSignedInAsync(client));
    }

    // ---- Status mapping convention -----------------------------------------------------------------------

    [Theory]
    [InlineData("workspaces.not_found", StatusCodes.Status404NotFound)]
    [InlineData("recipes.forbidden", StatusCodes.Status403Forbidden)]
    [InlineData("recipes.conflict", StatusCodes.Status409Conflict)]
    [InlineData("media.gone", StatusCodes.Status410Gone)]
    [InlineData("idempotency.key_reused", StatusCodes.Status422UnprocessableEntity)]
    [InlineData("auth.registration.invalid", StatusCodes.Status400BadRequest)]
    public void Application_codes_map_to_statuses_by_their_reason_suffix(string code, int status) =>
        Assert.Equal(status, ProblemResults.StatusFor(code));

    // ---- Helpers ---------------------------------------------------------------------------------------

    private static WebApplicationFactory<Gateway::Program> GatewayWithClock(StubApi api, TimeProvider time) =>
        GatewayTestHost.Create(apiSessions: () => api, configure: web =>
            web.ConfigureTestServices(services => services.AddSingleton(time)));

    private static HttpClient HttpsClientWithCookies(WebApplicationFactory<Gateway::Program> gateway) =>
        gateway.CreateDefaultClient(new Uri("https://localhost"), new CookieContainerHandler());

    private static async Task<HttpResponseMessage> PostLoginAsync(
        HttpClient client, string email = "cook@example.com", string password = "correct horse battery")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/login") { Content = JsonContent.Create(new { email, password }) };
        request.Headers.Add("X-XSRF-TOKEN", await AntiforgeryAsync(client));
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<bool> IsSignedInAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/bff/session", TestContext.Current.CancellationToken))
            .GetProperty("authenticated").GetBoolean();

    private static async Task<string> AntiforgeryAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/bff/antiforgery", TestContext.Current.CancellationToken))
            .GetProperty("requestToken").GetString()!;

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
    }

    /// <summary>The API's internal session routes: sign-in always succeeds; validation is scriptable.</summary>
    private sealed class StubApi : HttpMessageHandler
    {
        private const string User = """{"userId":"user-1","displayName":"Sam","roles":[],"securityStamp":"stamp-1"}""";

        public Func<HttpResponseMessage> ValidateResponse { get; set; } = () => Json(User);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/validate", StringComparison.Ordinal)
                ? ValidateResponse()
                : Json(User));

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused");
    }

    private sealed class CountingHandler(HttpMessageHandler inner, Action onSend) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onSend();
            return base.SendAsync(request, cancellationToken);
        }
    }
}
