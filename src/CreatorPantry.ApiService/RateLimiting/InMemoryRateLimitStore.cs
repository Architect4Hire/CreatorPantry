using System.Collections.Concurrent;
using CreatorPantry.Domain.Managers.Limits;

namespace CreatorPantry.ApiService.RateLimiting;

/// <summary>
/// Token buckets in this process. The store when no Redis is configured, and the fallback when Redis cannot be
/// reached — limits then hold per instance rather than across them, which the 15.3 review chose over either
/// refusing everything or limiting nothing.
/// </summary>
/// <remarks>
/// A bucket that has refilled completely is the same as no bucket, so one untouched for its whole refill time is
/// dropped; the dictionary cannot grow past the callers active in the last few minutes.
/// </remarks>
public sealed class InMemoryRateLimitStore : IRateLimitStore
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    private long _takes;

    public Task<RateLimitDecision> TryTakeAsync(
        string key, TokenBucketRule rule, int cost, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var charged = TokenBucketMath.Cost(rule, cost);
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket(rule.Burst, now));
        RateLimitDecision decision;

        lock (bucket)
        {
            var elapsed = Math.Max(0, (now - bucket.UpdatedAt).TotalMilliseconds);
            var tokens = Math.Min(rule.Burst, bucket.Tokens + elapsed * rule.RefillPerMillisecond);

            if (tokens >= charged)
            {
                tokens -= charged;
                decision = new RateLimitDecision(true, TimeSpan.Zero);
            }
            else
            {
                decision = new RateLimitDecision(false, TokenBucketMath.RetryAfter(rule, tokens, charged));
            }

            bucket.Tokens = tokens;
            bucket.UpdatedAt = now;
            bucket.ExpiresAt = now + rule.TimeToFull;
        }

        if (Interlocked.Increment(ref _takes) % 1024 == 0)
        {
            Prune(now);
        }

        return Task.FromResult(decision);
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, bucket) in _buckets)
        {
            if (bucket.ExpiresAt < now)
            {
                _buckets.TryRemove(new KeyValuePair<string, Bucket>(key, bucket));
            }
        }
    }

    private sealed class Bucket(double tokens, DateTimeOffset updatedAt)
    {
        public double Tokens { get; set; } = tokens;

        public DateTimeOffset UpdatedAt { get; set; } = updatedAt;

        public DateTimeOffset ExpiresAt { get; set; } = updatedAt;
    }
}
