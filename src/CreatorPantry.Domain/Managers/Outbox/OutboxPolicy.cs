namespace CreatorPantry.Domain.Managers.Outbox;

/// <summary>Limits and timing for the transactional outbox, shared by EF configuration and the dispatcher.</summary>
public static class OutboxPolicy
{
    public const int TypeMaxLength = 200;
    public const int LastErrorMaxLength = 2000;

    /// <summary>How many due messages one dispatch pass claims at once.</summary>
    public const int BatchSize = 20;

    /// <summary>Attempts before a message is Poisoned instead of retried again.</summary>
    public const int MaxAttempts = 5;

    /// <summary>How long a claim on a message holds before another dispatch pass may reclaim it.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>How often the Worker's background dispatch loop runs.</summary>
    public static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

    /// <summary>Doubling backoff after a failed attempt: 10s, 20s, 40s, ... capped at 30 minutes.</summary>
    public static TimeSpan BackoffFor(int attempts)
    {
        var exponent = Math.Max(0, attempts - 1);
        var seconds = Math.Min(10 * Math.Pow(2, exponent), MaxBackoff.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }
}
