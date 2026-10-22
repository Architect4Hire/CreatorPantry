using CreatorPantry.Domain.Managers.Limits;

namespace CreatorPantry.ApiService.RateLimiting;

/// <param name="Allowed">Whether the tokens were taken.</param>
/// <param name="RetryAfter">How long until the request could be afforded; zero when it was.</param>
/// <param name="FellBack">The shared store could not be asked, and this instance's own bucket answered.</param>
public readonly record struct RateLimitDecision(bool Allowed, TimeSpan RetryAfter, bool FellBack = false);

/// <summary>
/// Token buckets, one per key. Taking is atomic: two requests racing for the last token cannot both get it.
/// </summary>
public interface IRateLimitStore
{
    /// <param name="cost">At least one; more than the bucket holds is charged as the whole bucket.</param>
    Task<RateLimitDecision> TryTakeAsync(
        string key, TokenBucketRule rule, int cost, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>The arithmetic both stores share, so the in-memory fallback answers exactly as Redis would.</summary>
internal static class TokenBucketMath
{
    public static int Cost(TokenBucketRule rule, int cost) => Math.Clamp(cost, 1, rule.Burst);

    /// <summary>The wait for <paramref name="cost"/> tokens when only <paramref name="tokens"/> are there.</summary>
    public static TimeSpan RetryAfter(TokenBucketRule rule, double tokens, int cost) =>
        TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling((cost - tokens) / rule.RefillPerMillisecond)));
}
