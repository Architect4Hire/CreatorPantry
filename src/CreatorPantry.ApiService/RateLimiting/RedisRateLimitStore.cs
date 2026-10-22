using System.Globalization;
using CreatorPantry.Domain.Managers.Limits;
using StackExchange.Redis;

namespace CreatorPantry.ApiService.RateLimiting;

/// <summary>
/// Token buckets in Redis, shared by every API instance, each taken in one atomic script.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One round trip, no race.</strong> The script reads the bucket, refills it for the time since it was last
/// touched, takes the tokens if they are there, writes it back and sets it to expire once it would be full again —
/// all inside Redis, so two instances racing for the last token cannot both have it.
/// </para>
/// <para>
/// <strong>The application's clock, not Redis's.</strong> The instant is passed in, which is what lets a test move
/// time; instances agree to well within the second that any limit here is measured in.
/// </para>
/// <para>
/// <strong>Redis unreachable is not an outage.</strong> The 15.3 review chose a per-instance fallback: the same
/// bucket arithmetic in this process, a warning in the log (once a minute, not once a request), and a
/// <c>fallback</c> decision on the limits metric. Limits still roughly hold; nothing a creator does stops working.
/// </para>
/// </remarks>
public sealed class RedisRateLimitStore(
    IConnectionMultiplexer redis,
    InMemoryRateLimitStore fallback,
    ILogger<RedisRateLimitStore> logger) : IRateLimitStore
{
    // KEYS[1] bucket; ARGV: capacity, refill per ms, now (unix ms), cost, ttl ms.
    // Returns { allowed (1|0), tokens left as a string }: Lua numbers lose their fraction on the way out as integers.
    private static readonly LuaScript TakeScript = LuaScript.Prepare(
        """
        local data = redis.call('HMGET', @key, 't', 'ts')
        local capacity = tonumber(@capacity)
        local rate = tonumber(@rate)
        local now = tonumber(@now)
        local cost = tonumber(@cost)
        local tokens = tonumber(data[1])
        local ts = tonumber(data[2])
        if tokens == nil or ts == nil then
          tokens = capacity
          ts = now
        end
        local elapsed = now - ts
        if elapsed < 0 then elapsed = 0 end
        tokens = math.min(capacity, tokens + elapsed * rate)
        local allowed = 0
        if tokens >= cost then
          tokens = tokens - cost
          allowed = 1
        end
        redis.call('HSET', @key, 't', tostring(tokens), 'ts', tostring(now))
        redis.call('PEXPIRE', @key, @ttl)
        return { allowed, tostring(tokens) }
        """);

    /// <summary>
    /// The longest a limit check waits for Redis. A hung connection is treated as an unreachable one, so a request
    /// is never held for the client library's own multi-second timeout to say what the fallback can say now.
    /// </summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMilliseconds(250);

    private long _lastWarningTicks;

    public async Task<RateLimitDecision> TryTakeAsync(
        string key, TokenBucketRule rule, int cost, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var charged = TokenBucketMath.Cost(rule, cost);

        try
        {
            var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(
                TakeScript,
                new
                {
                    key = (RedisKey)key,
                    capacity = rule.Burst,
                    rate = rule.RefillPerMillisecond.ToString("R", CultureInfo.InvariantCulture),
                    now = now.ToUnixTimeMilliseconds(),
                    cost = charged,
                    ttl = (long)rule.TimeToFull.TotalMilliseconds + 1000,
                }).WaitAsync(CallTimeout, cancellationToken))!;

            var tokens = double.Parse((string)result[1]!, CultureInfo.InvariantCulture);

            return (int)result[0] == 1
                ? new RateLimitDecision(true, TimeSpan.Zero)
                : new RateLimitDecision(false, TokenBucketMath.RetryAfter(rule, tokens, charged));
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException or ObjectDisposedException)
        {
            WarnAtMostOncePerMinute(exception);

            return await fallback.TryTakeAsync(key, rule, cost, now, cancellationToken) with { FellBack = true };
        }
    }

    private void WarnAtMostOncePerMinute(Exception exception)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastWarningTicks);

        if (now - last >= 60_000 && Interlocked.CompareExchange(ref _lastWarningTicks, now, last) == last)
        {
            logger.LogWarning(
                "Rate limits could not reach Redis ({ExceptionType}); each API instance is limiting on its own until it can.",
                exception.GetType().Name);
        }
    }
}
