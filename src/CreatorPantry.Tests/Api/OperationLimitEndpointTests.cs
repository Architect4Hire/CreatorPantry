extern alias ApiService;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ApiService::CreatorPantry.ApiService.RateLimiting;
using CreatorPantry.Domain.Managers.Limits;
using CreatorPantry.Tests.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Api;

/// <summary>
/// The rate limits (15.3) as a browser meets them, through the real gateway: burst, refill, the refusal's shape,
/// and buckets that belong to one user or one workspace and never leak across.
/// </summary>
/// <remarks>
/// Time moves inside the bucket store only. The API's own clock also dates tokens and sessions, and moving it
/// would test the sign-in rather than the limit.
/// </remarks>
public sealed class OperationLimitEndpointTests : IAsyncLifetime
{
    private readonly ShiftedStore _store = new();
    private TwoWorkspaceGatewayFixture _fixture = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IRateLimitStore>();
            services.AddSingleton<IRateLimitStore>(_store);
            services.Configure<LimitsOptions>(options =>
            {
                options.Enabled = true;

                // Small and slow, so a test reaches each edge in a few requests: three at once for a person and
                // five for a workspace, each refilling one a minute.
                options.Rates[LimitedOperation.Search] = new OperationRateRule
                {
                    User = TokenBucketRule.PerMinute(3, 1), Workspace = TokenBucketRule.PerMinute(5, 1),
                };
                options.Rates[LimitedOperation.WorkspaceCreate] = new OperationRateRule
                {
                    User = new TokenBucketRule { Burst = 1, Refill = 1, Per = TimeSpan.FromHours(1) },
                };
            });
        });

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string RecipesIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/recipes";

    // ---- burst and reset ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_burst_past_the_users_bucket_is_refused_with_when_to_come_back()
    {
        using var owner = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        for (var request = 0; request < 3; request++)
        {
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(RecipesIn(_fixture.WorkspaceA), Ct)).StatusCode);
        }

        var refused = await owner.GetAsync(RecipesIn(_fixture.WorkspaceA), Ct);
        var body = await BodyAsync(refused);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(LimitErrorCodes.RateExhausted, body.GetProperty("code").GetString());
        Assert.Equal("later", body.GetProperty("retry").GetString());
        Assert.Equal("Search", body.GetProperty("operation").GetString());
        Assert.Equal("User", body.GetProperty("scope").GetString());
        Assert.Equal(3, body.GetProperty("limit").GetInt32());

        // One token a minute: the next one is a minute away, said both in the body and in the header.
        var resetsAt = body.GetProperty("resetsAt").GetDateTimeOffset();
        Assert.InRange(resetsAt - DateTimeOffset.UtcNow, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(61));
        Assert.Equal(resetsAt.ToUnixTimeSeconds(), refused.Headers.RetryAfter!.Date!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task The_bucket_refills_with_time_and_only_as_fast_as_its_rate()
    {
        using var owner = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        for (var request = 0; request < 3; request++)
        {
            await owner.GetAsync(RecipesIn(_fixture.WorkspaceA), Ct);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await owner.GetAsync(RecipesIn(_fixture.WorkspaceA), Ct)).StatusCode);

        _store.Advance(TimeSpan.FromSeconds(61));

        // One minute buys one request, not a fresh burst.
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(RecipesIn(_fixture.WorkspaceA), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await owner.GetAsync(RecipesIn(_fixture.WorkspaceA), Ct)).StatusCode);

        _store.Advance(TimeSpan.FromMinutes(10));

        for (var request = 0; request < 3; request++)
        {
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(RecipesIn(_fixture.WorkspaceA), Ct)).StatusCode);
        }
    }

    // ---- isolation ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_users_bucket_is_theirs_alone()
    {
        using var owner = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var colleague = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        for (var request = 0; request < 3; request++)
        {
            await owner.GetAsync(RecipesIn(_fixture.WorkspaceA), Ct);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await owner.GetAsync(RecipesIn(_fixture.WorkspaceA), Ct)).StatusCode);

        // The same workspace, another person: their own full bucket.
        Assert.Equal(HttpStatusCode.OK, (await colleague.GetAsync(RecipesIn(_fixture.WorkspaceA), Ct)).StatusCode);
    }

    [Fact]
    public async Task A_workspace_bucket_is_shared_by_its_members_and_by_no_other_workspace()
    {
        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var memberA = await SignInAsync(_fixture.WorkspaceA.MemberEmail);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        // Five between them, three of them the owner's: the workspace's burst is spent.
        for (var request = 0; request < 3; request++)
        {
            Assert.Equal(HttpStatusCode.OK, (await ownerA.GetAsync(RecipesIn(_fixture.WorkspaceA), Ct)).StatusCode);
        }

        for (var request = 0; request < 2; request++)
        {
            Assert.Equal(HttpStatusCode.OK, (await memberA.GetAsync(RecipesIn(_fixture.WorkspaceA), Ct)).StatusCode);
        }

        // The member still has a token of their own, and the workspace has none.
        var refused = await memberA.GetAsync(RecipesIn(_fixture.WorkspaceA), Ct);
        var body = await BodyAsync(refused);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("Workspace", body.GetProperty("scope").GetString());
        Assert.Equal(5, body.GetProperty("limit").GetInt32());

        // Nothing in it names a workspace, a member or an account — A's or anyone else's.
        var text = body.GetRawText();
        foreach (var secret in new[]
        {
            _fixture.WorkspaceA.Id.ToString(), _fixture.WorkspaceA.Slug, _fixture.WorkspaceB.Id.ToString(),
            _fixture.WorkspaceB.Slug, _fixture.WorkspaceA.OwnerEmail, _fixture.WorkspaceA.MemberEmail,
        })
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }

        // B's bucket is B's: untouched by everything A did.
        for (var request = 0; request < 3; request++)
        {
            Assert.Equal(HttpStatusCode.OK, (await ownerB.GetAsync(RecipesIn(_fixture.WorkspaceB), Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task Another_workspaces_slug_is_refused_before_it_could_spend_anyones_bucket()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        // B's owner is no member of A: every attempt is the not-found a stranger gets, and none of it is A's.
        for (var request = 0; request < 10; request++)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await ownerB.GetAsync(RecipesIn(_fixture.WorkspaceA), Ct)).StatusCode);
        }

        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        Assert.Equal(HttpStatusCode.OK, (await ownerA.GetAsync(RecipesIn(_fixture.WorkspaceA), Ct)).StatusCode);
    }

    // ---- no workspace -----------------------------------------------------------------------------------

    [Fact]
    public async Task Creating_workspaces_is_limited_per_person_before_they_belong_to_any()
    {
        // Each owner's one creation this hour was spent seeding the fixture, so a second is refused.
        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var refused = await ownerA.PostAsJsonAsync("/api/v1/workspaces", new { Name = "A second kitchen" }, Ct);
        var body = await BodyAsync(refused);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("WorkspaceCreate", body.GetProperty("operation").GetString());
        Assert.Equal("User", body.GetProperty("scope").GetString());

        _store.Advance(TimeSpan.FromHours(1));

        var allowed = await ownerA.PostAsJsonAsync("/api/v1/workspaces", new { Name = "A second kitchen" }, Ct);
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private Task<GatewayClient> SignInAsync(string email) => _fixture.SignInAsync(email, cancellationToken: Ct);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    /// <summary>The real in-memory buckets, asked as if it were later than it is.</summary>
    private sealed class ShiftedStore : IRateLimitStore
    {
        private readonly InMemoryRateLimitStore _inner = new();

        private long _offsetTicks;

        public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

        public Task<RateLimitDecision> TryTakeAsync(
            string key, TokenBucketRule rule, int cost, DateTimeOffset now, CancellationToken cancellationToken) =>
            _inner.TryTakeAsync(key, rule, cost, now.AddTicks(Interlocked.Read(ref _offsetTicks)), cancellationToken);
    }
}
