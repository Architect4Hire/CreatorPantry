extern alias Gateway;
extern alias Web;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CreatorPantry.Domain.Auth;
using CreatorPantry.Domain.Data;
using Gateway::CreatorPantry.Gateway.InternalTokens;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CreatorPantry.Tests.Gateway;

/// <summary>
/// Prompt 1.10: end-to-end edge verification. Every request goes browser → gateway → API exactly as in
/// production (real gateway pipeline, real API over SQLite); nothing calls a controller directly. A recording
/// handler captures everything the browser receives so token absence is asserted across whole journeys.
/// </summary>
public sealed partial class EdgeVerificationTests : IAsyncLifetime
{
    private const string Email = "cook@example.com";
    private const string Password = "correct horse battery";

    private SqliteApiHost _api = null!;
    private string _userId = null!;

    public async ValueTask InitializeAsync()
    {
        _api = await SqliteApiHost.StartAsync();
        _userId = await _api.CreateUserAsync(Email, Password);
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    [Fact]
    public async Task Registration_through_the_gateway_creates_an_unconfirmed_account()
    {
        using var gateway = CreateGateway();
        var browser = Browser(gateway);

        var registered = await browser.PostAsync("/api/v1/auth/register",
            new { email = "new@example.com", password = Password, displayName = "New" });
        var again = await browser.PostAsync("/api/v1/auth/register",
            new { email = "new@example.com", password = Password, displayName = "New" });
        var signIn = await browser.PostAsync("/bff/login", new { email = "new@example.com", password = Password });

        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        Assert.Equal("pending_confirmation", (await Json(registered)).GetProperty("status").GetString());
        Assert.Equal(await registered.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), await again.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AuthErrorCodes.SignInEmailUnconfirmed, (await Json(signIn)).GetProperty("code").GetString());
        browser.AssertNoTokensReachedTheBrowser(SecretsToWatch());
    }

    [Fact]
    public async Task Signed_in_api_calls_reach_the_api_as_the_user()
    {
        using var gateway = CreateGateway();
        var browser = Browser(gateway);
        await browser.SignInAsync(Email, Password);

        var changed = await browser.PostAsync("/api/v1/account/password",
            new { currentPassword = Password, newPassword = "a brand new passphrase" });

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal("password_changed", (await Json(changed)).GetProperty("status").GetString());
        Assert.True(await PasswordIsAsync("a brand new passphrase")); // the API acted on the signed-in user
        browser.AssertNoTokensReachedTheBrowser(SecretsToWatch());
    }

    [Fact]
    public async Task Anonymous_calls_to_protected_routes_get_401_problem_details()
    {
        using var gateway = CreateGateway();
        var browser = Browser(gateway);

        var response = await browser.PostAsync("/api/v1/account/password",
            new { currentPassword = Password, newPassword = "a brand new passphrase" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthorized", (await Json(response)).GetProperty("code").GetString());
        Assert.True(await PasswordIsAsync(Password));
    }

    [Fact]
    public async Task Forged_credentials_from_the_browser_never_authenticate_even_a_genuinely_signed_token()
    {
        using var gateway = CreateGateway();
        var browser = Browser(gateway);
        // The worst case: a real, gateway-signed user token leaked to an attacker.
        var leaked = gateway.Services.GetRequiredService<IInternalTokenIssuer>().Issue(new SessionPrincipal(_userId, "s1", []));

        var response = await browser.PostAsync("/api/v1/account/password",
            new { currentPassword = Password, newPassword = "a brand new passphrase" },
            request =>
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {leaked}");
                request.Headers.TryAddWithoutValidation("X-Forwarded-For", "10.0.0.1");
                request.Headers.TryAddWithoutValidation("X-MS-CLIENT-PRINCIPAL-ID", _userId);
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(await PasswordIsAsync(Password));
    }

    [Fact]
    public async Task Missing_antiforgery_token_is_rejected_at_the_edge_and_the_api_is_untouched()
    {
        using var gateway = CreateGateway();
        var browser = Browser(gateway);
        await browser.SignInAsync(Email, Password);

        var response = await browser.PostAsync("/api/v1/account/password",
            new { currentPassword = Password, newPassword = "a brand new passphrase" }, withAntiforgery: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("csrf.invalid", (await Json(response)).GetProperty("code").GetString());
        Assert.True(await PasswordIsAsync(Password));
    }

    [Fact]
    public async Task Sessions_refresh_by_sliding_within_the_idle_window_and_end_at_the_absolute_limit()
    {
        // One clock for gateway and API, so revalidation (and its tokens) runs normally throughout.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await SqliteApiHost.StartAsync(time);
        await api.CreateUserAsync(Email, Password);
        using var gateway = CreateGateway(api, time);
        var browser = Browser(gateway);
        await browser.SignInAsync(Email, Password);

        // Active every 7 hours: each request lands inside the 8-hour idle window and slides it forward.
        var elapsed = TimeSpan.Zero;
        while (elapsed + TimeSpan.FromHours(7) < TimeSpan.FromDays(7))
        {
            time.Advance(TimeSpan.FromHours(7));
            elapsed += TimeSpan.FromHours(7);
            Assert.True(await browser.IsSignedInAsync(), $"Session ended early after {elapsed}.");
        }

        time.Advance(TimeSpan.FromHours(7)); // past 7 days, although still active
        Assert.False(await browser.IsSignedInAsync());
    }

    [Fact]
    public async Task Sessions_expire_after_eight_idle_hours()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await SqliteApiHost.StartAsync(time);
        await api.CreateUserAsync(Email, Password);
        using var gateway = CreateGateway(api, time);
        var browser = Browser(gateway);
        await browser.SignInAsync(Email, Password);

        time.Advance(TimeSpan.FromHours(7.5));
        var withinIdle = await browser.IsSignedInAsync();
        time.Advance(TimeSpan.FromHours(8.5));
        var afterIdle = await browser.IsSignedInAsync();

        Assert.True(withinIdle);
        Assert.False(afterIdle);
    }

    [Fact]
    public async Task Server_side_revalidation_ends_every_session_after_a_password_change()
    {
        using var gateway = CreateGateway(revalidation: TimeSpan.Zero);
        var laptop = Browser(gateway);
        var phone = Browser(gateway);
        await laptop.SignInAsync(Email, Password);
        await phone.SignInAsync(Email, Password);

        await laptop.PostAsync("/api/v1/account/password", new { currentPassword = Password, newPassword = "a brand new passphrase" });

        Assert.False(await phone.IsSignedInAsync());
        Assert.False(await laptop.IsSignedInAsync());
    }

    [Fact]
    public async Task Logout_revokes_the_session_so_the_old_cookie_cannot_call_the_api()
    {
        using var gateway = CreateGateway();
        var browser = Browser(gateway);
        await browser.SignInAsync(Email, Password);
        var copiedCookie = browser.SessionCookie!;

        var logout = await browser.PostAsync("/bff/logout", new { });
        var attacker = Browser(gateway);
        var replay = await attacker.PostAsync("/api/v1/account/password",
            new { currentPassword = Password, newPassword = "a brand new passphrase" },
            request => request.Headers.Add("Cookie", $"__Host-creatorpantry-session={copiedCookie}"));

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.False(await browser.IsSignedInAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.True(await PasswordIsAsync(Password));
    }

    [Fact]
    public async Task Across_a_whole_journey_the_browser_never_receives_a_token_stamp_or_readable_cookie()
    {
        using var gateway = CreateGateway();
        var browser = Browser(gateway);

        await browser.GetAsync("/bff/session");
        await browser.PostAsync("/api/v1/auth/password-reset", new { email = Email });
        await browser.SignInAsync(Email, Password);
        await browser.GetAsync("/bff/session");
        await browser.GetAsync("/api/health");
        await browser.PostAsync("/api/v1/account/password", new { currentPassword = "wrong", newPassword = "a brand new passphrase" });
        await browser.PostAsync("/bff/logout", new { });

        Assert.True(browser.Responses.Count >= 8);
        browser.AssertNoTokensReachedTheBrowser(SecretsToWatch());
        Assert.All(browser.SetCookies, cookie => Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Web_host_gives_the_browser_only_the_gateway_address()
    {
        using var web = new WebApplicationFactory<Web::Program>()
            .WithWebHostBuilder(host => host.UseSetting("Client:GatewayUrl", "https://gateway.creatorpantry.test"));
        using var client = web.CreateClient();

        var config = await client.GetFromJsonAsync<JsonElement>("/runtime-config.json", TestContext.Current.CancellationToken);

        Assert.Equal(["gatewayUrl"], config.EnumerateObject().Select(property => property.Name));
        Assert.Equal("https://gateway.creatorpantry.test", config.GetProperty("gatewayUrl").GetString());
    }

    // ---- Helpers ---------------------------------------------------------------------------------------

    private WebApplicationFactory<Gateway::Program> CreateGateway(Action<IWebHostBuilder>? configure = null, TimeSpan? revalidation = null) =>
        GatewayTestHost.Create(
            proxyDownstream: _api.Factory.Server.CreateHandler,
            apiSessions: _api.Factory.Server.CreateHandler,
            configure: web =>
            {
                if (revalidation is not null)
                {
                    web.UseSetting("Session:RevalidationInterval", revalidation.Value.ToString());
                }

                configure?.Invoke(web);
            });

    private static WebApplicationFactory<Gateway::Program> CreateGateway(SqliteApiHost api, TimeProvider time) =>
        GatewayTestHost.Create(
            proxyDownstream: api.Factory.Server.CreateHandler,
            apiSessions: api.Factory.Server.CreateHandler,
            configure: web => web.ConfigureTestServices(services => services.AddSingleton(time)));

    private static BrowserClient Browser(WebApplicationFactory<Gateway::Program> gateway) => new(gateway);

    private IEnumerable<string> SecretsToWatch() => [_userId, Password];

    private async Task<bool> PasswordIsAsync(string password)
    {
        await using var scope = _api.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await users.CheckPasswordAsync((await users.FindByIdAsync(_userId))!, password);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    /// <summary>A browser: cookie jar, antiforgery handling like the SPA, and a record of everything it received.</summary>
    private sealed partial class BrowserClient
    {
        private const string SessionCookieName = "__Host-creatorpantry-session";
        private readonly HttpClient _client;
        private readonly RecordingHandler _recorder = new();
        private string? _antiforgery;

        public BrowserClient(WebApplicationFactory<Gateway::Program> gateway) =>
            _client = gateway.CreateDefaultClient(new Uri("https://localhost"), _recorder, new CookieContainerHandler());

        public IReadOnlyList<(HttpResponseMessage Response, string Body)> Responses => _recorder.Responses;

        public IEnumerable<string> SetCookies => _recorder.Responses
            .SelectMany(entry => entry.Response.Headers.TryGetValues("Set-Cookie", out var values) ? values : []);

        public string? SessionCookie => SetCookies
            .Where(cookie => cookie.StartsWith(SessionCookieName + "=", StringComparison.Ordinal))
            .Select(cookie => cookie.Split(';')[0][(SessionCookieName.Length + 1)..])
            .LastOrDefault(value => value.Length > 0);

        public Task<HttpResponseMessage> GetAsync(string path) => _client.GetAsync(path, TestContext.Current.CancellationToken);

        public async Task<HttpResponseMessage> PostAsync(
            string path, object body, Action<HttpRequestMessage>? configure = null, bool withAntiforgery = true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            if (withAntiforgery)
            {
                request.Headers.Add("X-XSRF-TOKEN", _antiforgery ??= await FetchAntiforgeryAsync());
            }

            configure?.Invoke(request);
            var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

            if (path == "/bff/login" && response.IsSuccessStatusCode)
            {
                // Like the SPA: the token is bound to the new identity.
                _antiforgery = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestToken").GetString();
            }
            else if (path == "/bff/logout")
            {
                _antiforgery = null;
            }

            return response;
        }

        public async Task SignInAsync(string email, string password)
        {
            var response = await PostAsync("/bff/login", new { email, password });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public async Task<bool> IsSignedInAsync() =>
            (await (await GetAsync("/bff/session")).Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
                .GetProperty("authenticated").GetBoolean();

        public void AssertNoTokensReachedTheBrowser(IEnumerable<string> secrets)
        {
            var secretList = secrets.ToList();
            foreach (var (response, body) in Responses)
            {
                var headers = string.Join("\n", response.Headers.Concat(response.Content.Headers)
                    .Where(header => !header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                    .Select(header => $"{header.Key}: {string.Join(",", header.Value)}"));
                var cookies = string.Join("\n", response.Headers.TryGetValues("Set-Cookie", out var values) ? values : []);

                Assert.False(response.Headers.Contains("Authorization"));
                Assert.False(Jwt().IsMatch(body + headers + cookies), $"A token reached the browser: {response.RequestMessage?.RequestUri}");
                Assert.All(secretList, secret => Assert.DoesNotContain(secret, body + headers));
                Assert.DoesNotContain("securityStamp", body, StringComparison.OrdinalIgnoreCase);
            }
        }

        private async Task<string> FetchAntiforgeryAsync() =>
            (await _client.GetFromJsonAsync<JsonElement>("/bff/antiforgery", TestContext.Current.CancellationToken))
                .GetProperty("requestToken").GetString()!;

        [GeneratedRegex(@"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+")]
        private static partial Regex Jwt();

        private sealed class RecordingHandler : DelegatingHandler
        {
            public List<(HttpResponseMessage Response, string Body)> Responses { get; } = [];

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = await base.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                lock (Responses)
                {
                    Responses.Add((response, body));
                }

                return response;
            }
        }
    }
}
