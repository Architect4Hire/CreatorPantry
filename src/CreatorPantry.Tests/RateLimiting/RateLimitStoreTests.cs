extern alias ApiService;

using ApiService::CreatorPantry.ApiService.RateLimiting;
using CreatorPantry.Domain.Managers.Limits;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace CreatorPantry.Tests.RateLimiting;

/// <summary>
/// What every rate-limit store must do (15.3): a burst, then a refusal that says how long, a refill at the rule's
/// rate and no faster, a cost the bucket cannot hold charged as the whole bucket, and keys that never share tokens.
/// </summary>
public abstract class RateLimitStoreContract
{
    protected static readonly DateTimeOffset Start = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Three at once, one a minute back.</summary>
    protected static readonly TokenBucketRule Rule = TokenBucketRule.PerMinute(3, 1);

    protected abstract IRateLimitStore Store { get; }

    protected static string NewKey() => $"account:{Guid.NewGuid():N}:limits:Search";

    [Fact]
    public async Task A_full_bucket_gives_its_burst_then_refuses_with_the_wait()
    {
        var key = NewKey();

        for (var take = 0; take < 3; take++)
        {
            Assert.True((await Store.TryTakeAsync(key, Rule, 1, Start, Ct)).Allowed);
        }

        var refused = await Store.TryTakeAsync(key, Rule, 1, Start, Ct);

        Assert.False(refused.Allowed);
        Assert.InRange(refused.RetryAfter, TimeSpan.FromSeconds(59), TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task It_refills_at_the_rule_and_no_faster()
    {
        var key = NewKey();

        for (var take = 0; take < 3; take++)
        {
            await Store.TryTakeAsync(key, Rule, 1, Start, Ct);
        }

        // Thirty seconds is half a token: still refused, and the wait is the other half.
        var half = await Store.TryTakeAsync(key, Rule, 1, Start.AddSeconds(30), Ct);
        Assert.False(half.Allowed);
        Assert.InRange(half.RetryAfter, TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(31));

        Assert.True((await Store.TryTakeAsync(key, Rule, 1, Start.AddSeconds(61), Ct)).Allowed);
        Assert.False((await Store.TryTakeAsync(key, Rule, 1, Start.AddSeconds(62), Ct)).Allowed);

        // Hours later the bucket is full again — and only full: the burst, not every minute that passed.
        var later = Start.AddHours(3);
        for (var take = 0; take < 3; take++)
        {
            Assert.True((await Store.TryTakeAsync(key, Rule, 1, later, Ct)).Allowed);
        }

        Assert.False((await Store.TryTakeAsync(key, Rule, 1, later, Ct)).Allowed);
    }

    [Fact]
    public async Task A_cost_larger_than_the_bucket_takes_the_whole_bucket_rather_than_waiting_forever()
    {
        var key = NewKey();

        Assert.True((await Store.TryTakeAsync(key, Rule, 15, Start, Ct)).Allowed);
        Assert.False((await Store.TryTakeAsync(key, Rule, 1, Start, Ct)).Allowed);
    }

    [Fact]
    public async Task Two_keys_never_share_a_token()
    {
        var mine = NewKey();
        var theirs = NewKey();

        for (var take = 0; take < 3; take++)
        {
            await Store.TryTakeAsync(mine, Rule, 1, Start, Ct);
        }

        Assert.False((await Store.TryTakeAsync(mine, Rule, 1, Start, Ct)).Allowed);
        Assert.True((await Store.TryTakeAsync(theirs, Rule, 1, Start, Ct)).Allowed);
    }

    [Fact]
    public async Task Requests_racing_for_the_last_tokens_get_exactly_the_burst()
    {
        var key = NewKey();
        var rule = TokenBucketRule.PerMinute(10, 1);

        var decisions = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => Store.TryTakeAsync(key, rule, 1, Start, Ct))));

        Assert.Equal(10, decisions.Count(decision => decision.Allowed));
    }
}

public sealed class InMemoryRateLimitStoreTests : RateLimitStoreContract
{
    protected override IRateLimitStore Store { get; } = new InMemoryRateLimitStore();
}

/// <summary>One Redis for the class: the store is stateless beyond its keys, and every test uses fresh ones.</summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder().Build();

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _redis.StartAsync();
        Connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        await _redis.DisposeAsync();
    }
}

public sealed class RedisRateLimitStoreTests(RedisFixture redis) : RateLimitStoreContract, IClassFixture<RedisFixture>
{
    protected override IRateLimitStore Store { get; } = new RedisRateLimitStore(
        redis.Connection, new InMemoryRateLimitStore(), NullLogger<RedisRateLimitStore>.Instance);

    [Fact]
    public async Task Every_instance_shares_one_bucket()
    {
        // Two stores over one Redis are two API instances: the burst is the caller's, not each instance's.
        var other = new RedisRateLimitStore(
            redis.Connection, new InMemoryRateLimitStore(), NullLogger<RedisRateLimitStore>.Instance);
        var key = NewKey();

        Assert.True((await Store.TryTakeAsync(key, Rule, 2, Start, Ct)).Allowed);
        Assert.True((await other.TryTakeAsync(key, Rule, 1, Start, Ct)).Allowed);
        Assert.False((await Store.TryTakeAsync(key, Rule, 1, Start, Ct)).Allowed);
        Assert.False((await other.TryTakeAsync(key, Rule, 1, Start, Ct)).Allowed);
    }

    [Fact]
    public async Task A_bucket_expires_once_it_would_be_full_again()
    {
        var key = NewKey();

        await Store.TryTakeAsync(key, Rule, 1, Start, Ct);

        var ttl = await redis.Connection.GetDatabase().KeyTimeToLiveAsync(key);

        // Three minutes to refill three tokens at one a minute, plus the store's second of slack.
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(2));
    }
}

/// <summary>
/// Redis unreachable (15.3): each instance limits on its own, says so, and nothing a creator does stops working.
/// </summary>
public sealed class RedisFallbackTests : IAsyncDisposable
{
    private readonly ConnectionMultiplexer _nowhere = ConnectionMultiplexer.Connect(new ConfigurationOptions
    {
        EndPoints = { "127.0.0.1:1" },
        AbortOnConnectFail = false,
        ConnectTimeout = 200,
        SyncTimeout = 200,
        AsyncTimeout = 200,
        ConnectRetry = 0,
    });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _nowhere.DisposeAsync();

    [Fact]
    public async Task An_unreachable_redis_falls_back_to_this_instances_buckets_and_still_limits()
    {
        var store = new RedisRateLimitStore(_nowhere, new InMemoryRateLimitStore(), NullLogger<RedisRateLimitStore>.Instance);
        var rule = TokenBucketRule.PerMinute(2, 1);
        var now = DateTimeOffset.UtcNow;
        var key = $"account:{Guid.NewGuid():N}:limits:Search";

        var first = await store.TryTakeAsync(key, rule, 1, now, Ct);
        var second = await store.TryTakeAsync(key, rule, 1, now, Ct);
        var third = await store.TryTakeAsync(key, rule, 1, now, Ct);

        Assert.True(first is { Allowed: true, FellBack: true });
        Assert.True(second.Allowed);
        Assert.True(third is { Allowed: false, FellBack: true });
    }
}
