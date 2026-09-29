using System.Net;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// The account-scoped usage routes, over the real API host: who may reach them, and what a caller cannot say
/// about whose usage it wants (USAGE-008).
/// </summary>
public sealed class AccountAiUsageRouteTests
{
    private const string Current = "/api/v1/me/ai-usage";
    private const string History = "/api/v1/me/ai-usage/history";

    /// <summary>
    /// Every action on this controller requires a session by default — <c>MapAuthenticatedControllers</c>, not
    /// an attribute somebody has to remember. An anonymous caller is turned away before an action runs, so
    /// there is no point at which an account has to be inferred.
    /// </summary>
    [Theory]
    [InlineData(Current)]
    [InlineData(History)]
    public async Task Reading_an_accounts_usage_without_a_session_is_refused(string route)
    {
        await using var host = await SqliteApiHost.StartAsync();
        var client = host.Factory.CreateClient();

        var response = await client.GetAsync(route, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Naming an account in the query string changes nothing, because nothing binds it — the refusal below is
    /// the same 401 an unadorned request gets, from the same place, for the same reason. USAGE-008 forbids
    /// taking an account from a route, body, query or header, and the way this contract meets that is by
    /// having nowhere to put one rather than by rejecting what it is sent.
    /// </summary>
    [Theory]
    [InlineData(Current + "?accountId=user-alex")]
    [InlineData(Current + "?userId=user-alex")]
    [InlineData(History + "?accountId=user-alex&limit=5")]
    public async Task Naming_another_account_in_the_query_string_changes_nothing(string route)
    {
        await using var host = await SqliteApiHost.StartAsync();
        var client = host.Factory.CreateClient();

        var response = await client.GetAsync(route, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A header is the other place an account id could plausibly arrive, and the answer is the same: there is
    /// nothing reading one, so it is neither honoured nor refused — it is simply not a thing this route has.
    /// </summary>
    [Fact]
    public async Task Naming_another_account_in_a_header_changes_nothing()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var client = host.Factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, Current);
        request.Headers.Add("X-Account-Id", "user-alex");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Not under <c>/workspaces/{workspaceSlug}</c>, and deliberately: the answer spans workspaces, so a
    /// workspace-scoped route could only narrow it to one or answer about workspaces it does not name.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/workspaces/cozy-fall/ai-usage")]
    [InlineData("/api/v1/workspaces/cozy-fall/me/ai-usage")]
    public async Task The_usage_routes_do_not_exist_under_a_workspace(string route)
    {
        await using var host = await SqliteApiHost.StartAsync();
        var client = host.Factory.CreateClient();

        var response = await client.GetAsync(route, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
