extern alias Gateway;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreatorPantry.Domain.Auth;
using CreatorPantry.ServiceDefaults;
using CreatorPantry.Tests.Auth;
using Gateway::CreatorPantry.Gateway.InternalTokens;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.Tests.Gateway;

/// <summary>
/// The browser boundary: the real gateway session in front of the real API (SQLite). The browser holds only
/// an opaque, hardened cookie; tokens exist only between gateway and API.
/// </summary>
public sealed class BffSessionTests : IAsyncLifetime
{
    private const string Email = "cook@example.com";
    private const string Password = "correct horse battery";
    private const string SessionCookie = "__Host-creatorpantry-session";

    private readonly CapturingLoggerProvider _gatewayLogs = new();
    private SqliteApiHost _api = null!;
    private WebApplication _echo = null!;
    private string _userId = null!;

    public async ValueTask InitializeAsync()
    {
        _api = await SqliteApiHost.StartAsync();
        _userId = await _api.CreateUserAsync(Email, Password);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _echo = builder.Build();
        _echo.Map("/{**path}", (HttpRequest request) => Results.Json(new GatewayProxyTests.EchoedRequest(
            request.Path.Value!,
            request.Headers.ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase))));
        await _echo.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _echo.DisposeAsync();
        await _api.DisposeAsync();
    }

    [Fact]
    public async Task Sign_in_issues_only_a_hardened_opaque_session_cookie()
    {
        using var gateway = CreateGateway();
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);

        var response = await LoginAsync(client, Email, Password);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = SessionSetCookie(response);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);

        var expires = DateTimeOffset.Parse(cookie.Split(';').Single(part => part.Trim().StartsWith("expires=", StringComparison.OrdinalIgnoreCase)).Split('=', 2)[1]);
        Assert.InRange(expires - DateTimeOffset.UtcNow, TimeSpan.FromHours(7.9), TimeSpan.FromHours(8.1)); // bounded idle lifetime

        var value = CookieValue(cookie);
        Assert.False(LooksLikeJwt(value), "The session cookie must be an opaque reference, not a token.");

        var json = JsonNode.Parse(body)!.AsObject();
        Assert.Equal(["authenticated", "displayName", "requestToken"], json.Select(property => property.Key).Order());
        Assert.False(LooksLikeJwt(body));
        Assert.DoesNotContain(_userId, body);
        Assert.DoesNotContain(await _api.SecurityStampAsync(_userId), body);
    }

    [Fact]
    public async Task Session_endpoint_reports_state_but_never_identifiers_or_tokens()
    {
        using var gateway = CreateGateway();
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);

        var before = await client.GetFromJsonAsync<JsonElement>("/bff/session", TestContext.Current.CancellationToken);
        await LoginAsync(client, Email, Password);
        var response = await client.GetAsync("/bff/session", TestContext.Current.CancellationToken);
        var after = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.False(before.GetProperty("authenticated").GetBoolean());
        Assert.True(JsonDocument.Parse(after).RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal("Sam", JsonDocument.Parse(after).RootElement.GetProperty("displayName").GetString());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(LooksLikeJwt(after));
        Assert.DoesNotContain(_userId, after);
    }

    [Fact]
    public async Task Proxied_requests_carry_a_user_token_to_the_api_that_never_returns_to_the_browser()
    {
        using var gateway = CreateGateway(proxyToEcho: true);
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);
        var login = await LoginAsync(client, Email, Password);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("requestToken").GetString()!;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/anything") { Content = JsonContent.Create(new { }) };
        request.Headers.Add("X-XSRF-TOKEN", token);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var echoed = await response.Content.ReadFromJsonAsync<GatewayProxyTests.EchoedRequest>(TestContext.Current.CancellationToken);

        var forwarded = new JsonWebToken(echoed!.Headers["Authorization"]["Bearer ".Length..]);
        Assert.Equal(_userId, forwarded.Subject);
        Assert.Equal(InternalTokenDefaults.UserTokenUse, forwarded.GetClaim(InternalTokenDefaults.TokenUseClaim).Value);
        Assert.False(echoed.Headers.ContainsKey("Cookie")); // the browser session never reaches the API

        Assert.False(response.Headers.Contains("Authorization"));
        Assert.DoesNotContain(response.Headers, header => header.Value.Any(LooksLikeJwt));
    }

    [Fact]
    public async Task Logout_revokes_the_session_server_side_so_a_copied_cookie_stops_working()
    {
        using var gateway = CreateGateway();
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);
        var login = await LoginAsync(client, Email, Password);
        var copiedCookie = CookieValue(SessionSetCookie(login));
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("requestToken").GetString()!;

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/bff/logout");
        logout.Headers.Add("X-XSRF-TOKEN", token);
        var logoutResponse = await client.SendAsync(logout, TestContext.Current.CancellationToken);

        // Replay the stolen cookie from a fresh client.
        using var attacker = gateway.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        using var replay = new HttpRequestMessage(HttpMethod.Get, "/bff/session");
        replay.Headers.Add("Cookie", $"{SessionCookie}={copiedCookie}");
        var session = await (await attacker.SendAsync(replay, TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);
        Assert.False(session.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task Unsafe_requests_without_a_valid_antiforgery_token_are_rejected()
    {
        using var gateway = CreateGateway(proxyToEcho: true);
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);

        var loginWithoutToken = await client.PostAsJsonAsync("/bff/login", new { email = Email, password = Password },
            TestContext.Current.CancellationToken);

        var anonymousToken = await AntiforgeryTokenAsync(client);
        await LoginAsync(client, Email, Password);
        var apiWithoutToken = await client.PostAsJsonAsync("/api/v1/anything", new { }, TestContext.Current.CancellationToken);
        using var staleToken = new HttpRequestMessage(HttpMethod.Post, "/api/v1/anything") { Content = JsonContent.Create(new { }) };
        staleToken.Headers.Add("X-XSRF-TOKEN", anonymousToken); // issued before sign-in; bound to the anonymous user
        var apiWithStaleToken = await client.SendAsync(staleToken, TestContext.Current.CancellationToken);

        foreach (var response in new[] { loginWithoutToken, apiWithoutToken, apiWithStaleToken })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("csrf.invalid", (await ProblemAsync(response)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Unknown_email_wrong_password_and_lockout_are_indistinguishable()
    {
        using var gateway = CreateGateway();
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);

        var unknown = await PublicAsync(await LoginAsync(client, "nobody@example.com", Password));
        var wrong = await PublicAsync(await LoginAsync(client, Email, "not the password at all"));
        for (var attempt = 1; attempt < AccountPolicy.MaxFailedSignInAttempts; attempt++)
        {
            await LoginAsync(client, Email, "not the password at all");
        }

        var lockedWithCorrectPassword = await PublicAsync(await LoginAsync(client, Email, Password));

        Assert.Equal(HttpStatusCode.BadRequest, wrong.Status);
        Assert.Equal(AuthErrorCodes.SignInFailed, JsonDocument.Parse(wrong.Body).RootElement.GetProperty("code").GetString());
        Assert.Equal(wrong, unknown);
        Assert.Equal(wrong, lockedWithCorrectPassword);
    }

    [Fact]
    public async Task Unconfirmed_email_is_reported_only_after_the_correct_password()
    {
        await _api.CreateUserAsync("pending@example.com", Password, emailConfirmed: false);
        using var gateway = CreateGateway();
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);

        var correct = await ProblemAsync(await LoginAsync(client, "pending@example.com", Password));
        var incorrect = await ProblemAsync(await LoginAsync(client, "pending@example.com", "not the password at all"));

        Assert.Equal(AuthErrorCodes.SignInEmailUnconfirmed, correct.GetProperty("code").GetString());
        Assert.Equal(AuthErrorCodes.SignInFailed, incorrect.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Password_change_ends_existing_sessions_at_the_next_revalidation()
    {
        using var gateway = CreateGateway(revalidateEveryRequest: true);
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);
        await LoginAsync(client, Email, Password);
        var stillValid = await client.GetFromJsonAsync<JsonElement>("/bff/session", TestContext.Current.CancellationToken);

        await _api.ChangePasswordAsync(_userId, Password, "a brand new passphrase"); // rotates the security stamp
        var afterChange = await client.GetFromJsonAsync<JsonElement>("/bff/session", TestContext.Current.CancellationToken);

        Assert.True(stillValid.GetProperty("authenticated").GetBoolean());
        Assert.False(afterChange.GetProperty("authenticated").GetBoolean());
    }

    [Theory]
    [InlineData("/api/v1/internal/sessions")]
    [InlineData("/api/v1/internal/sessions/validate")]
    [InlineData("/API/V1/INTERNAL/sessions")]
    public async Task Internal_api_routes_are_not_reachable_through_the_gateway(string path)
    {
        using var gateway = CreateGateway();
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);
        await LoginAsync(client, Email, Password);
        var token = await AntiforgeryTokenAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new { email = Email, password = Password }) };
        request.Headers.Add("X-XSRF-TOKEN", token);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Internal_api_routes_accept_only_the_gateway_service_token()
    {
        using var gateway = CreateGateway();
        var issuer = gateway.Services.GetRequiredService<IInternalTokenIssuer>();
        using var api = _api.Factory.CreateClient();

        async Task<HttpStatusCode> CallAsync(string? bearer)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/internal/sessions")
            {
                Content = JsonContent.Create(new { email = Email, password = Password }),
            };
            if (bearer is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }

            return (await api.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(null));
        Assert.Equal(HttpStatusCode.Forbidden, await CallAsync(issuer.Issue(new SessionPrincipal(_userId, "s1", []))));
        Assert.Equal(HttpStatusCode.OK, await CallAsync(issuer.IssueService()));
    }

    [Fact]
    public async Task Sign_in_is_rate_limited_per_client_address()
    {
        using var gateway = CreateGateway();
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);

        HttpResponseMessage last = null!;
        for (var attempt = 0; attempt < 11; attempt++)
        {
            last = await LoginAsync(client, "nobody@example.com", Password);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
        Assert.Equal("rate_limited", (await ProblemAsync(last)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Passwords_cookies_tokens_and_stamps_never_reach_logs()
    {
        using var gateway = CreateGateway(proxyToEcho: true, revalidateEveryRequest: true);
        using var client = gateway.CreateClient(GatewayTestHost.HttpsClient);
        var login = await LoginAsync(client, Email, Password);
        var cookie = CookieValue(SessionSetCookie(login));
        await client.GetAsync("/bff/session", TestContext.Current.CancellationToken);
        await LoginAsync(client, Email, "a wrong password attempt");

        var secrets = new[] { Password, "a wrong password attempt", cookie, await _api.SecurityStampAsync(_userId) };
        var entries = _gatewayLogs.Entries.Concat(_api.Logs.Entries).ToList();

        Assert.NotEmpty(entries);
        Assert.All(entries, entry =>
        {
            Assert.All(secrets, secret => Assert.DoesNotContain(secret, entry));
            Assert.False(LooksLikeJwt(entry), "A token was logged.");
        });
    }

    private WebApplicationFactory<Gateway::Program> CreateGateway(bool proxyToEcho = false, bool revalidateEveryRequest = false) =>
        GatewayTestHost.Create(
            proxyDownstream: proxyToEcho ? () => _echo.GetTestServer().CreateHandler() : _api.Factory.Server.CreateHandler,
            apiSessions: _api.Factory.Server.CreateHandler,
            configure: web =>
            {
                web.ConfigureLogging(logging => logging.AddProvider(_gatewayLogs));
                if (revalidateEveryRequest)
                {
                    web.UseSetting("Session:RevalidationInterval", "00:00:00");
                }
            });

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/login") { Content = JsonContent.Create(new { email, password }) };
        request.Headers.Add("X-XSRF-TOKEN", await AntiforgeryTokenAsync(client));
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string> AntiforgeryTokenAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/bff/antiforgery", TestContext.Current.CancellationToken))
            .GetProperty("requestToken").GetString()!;

    private static string SessionSetCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(header => header.StartsWith(SessionCookie + "=", StringComparison.Ordinal));

    private static string CookieValue(string setCookie) => setCookie.Split(';')[0][(SessionCookie.Length + 1)..];

    private static bool LooksLikeJwt(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, @"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+");

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    /// <summary>Status and body with the per-request traceId removed.</summary>
    private static async Task<(HttpStatusCode Status, string Body)> PublicAsync(HttpResponseMessage response)
    {
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();
        node.Remove("traceId");
        return (response.StatusCode, node.ToJsonString());
    }
}
