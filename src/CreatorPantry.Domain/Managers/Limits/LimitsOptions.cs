namespace CreatorPantry.Domain.Managers.Limits;

/// <summary>
/// A token bucket: up to <see cref="Burst"/> at once, refilling <see cref="Refill"/> every <see cref="Per"/>.
/// </summary>
/// <remarks>
/// Burst is what a creator clicking quickly is allowed; the refill is what they may sustain. A request costing
/// more than the bucket holds is charged the whole bucket rather than refused forever.
/// </remarks>
public sealed record TokenBucketRule
{
    public int Burst { get; init; }

    public int Refill { get; init; }

    public TimeSpan Per { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Tokens added per millisecond.</summary>
    public double RefillPerMillisecond => Refill / Per.TotalMilliseconds;

    /// <summary>How long an empty bucket takes to fill, which is how long its state is worth keeping.</summary>
    public TimeSpan TimeToFull => TimeSpan.FromMilliseconds(Math.Ceiling(Burst / RefillPerMillisecond));

    public bool IsValid => Burst >= 1 && Refill >= 1 && Per > TimeSpan.Zero;

    public static TokenBucketRule PerMinute(int burst, int refill) => new() { Burst = burst, Refill = refill };
}

/// <summary>The rates one operation is held to. Either scope may be absent, and then it is not limited there.</summary>
public sealed record OperationRateRule
{
    public TokenBucketRule? User { get; init; }

    public TokenBucketRule? Workspace { get; init; }
}

/// <summary>
/// Every limit 15.3 enforces, in one section (<c>Limits</c>) the API and the Worker both bind and must agree on —
/// the same rule <c>Ai:Quota</c> already has.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Not billing.</strong> Nothing here is a plan, a price or a tier: one set of numbers for every workspace,
/// chosen to stop runaway use and to share the queues fairly. A plan's picture caps (AP.4.2) are the account's and
/// live in <c>Billing:Plans</c>; they apply beside these, and switch off with <see cref="Enabled"/>.
/// </para>
/// <para>
/// <strong>Three kinds, three stores.</strong> Rates are token buckets in Redis (an in-memory bucket per instance
/// when Redis is unreachable). In-progress caps and daily budgets are counted from the rows the work itself
/// writes, so they are exact, survive a restart, and are the numbers the worker re-checks.
/// </para>
/// </remarks>
public sealed class LimitsOptions
{
    public const string SectionName = "Limits";

    /// <summary>Off only in test hosts that are not about limits. Every deployed host enforces.</summary>
    public bool Enabled { get; set; } = true;

    public Dictionary<LimitedOperation, OperationRateRule> Rates { get; set; } = DefaultRates();

    /// <summary>AI operations queued or running for one member of a workspace.</summary>
    public int AiInFlightPerMember { get; set; } = 10;

    /// <summary>AI operations queued or running in one workspace.</summary>
    public int AiInFlightPerWorkspace { get; set; } = 20;

    /// <summary>AI operations one workspace may have running at once; the rest wait their turn behind other workspaces.</summary>
    public int AiRunningPerWorkspace { get; set; } = 4;

    /// <summary>Image requests queued or running in one workspace.</summary>
    public int ImageInFlightPerWorkspace { get; set; } = 3;

    /// <summary>Image requests one workspace may have running at once.</summary>
    /// <remarks>
    /// Three since AP.4.2, so the most generous plan's <c>RunningImages</c> can be reached by a creator who works in
    /// one workspace; startup refuses a plan above it. A plan's own cap, where there is one, is the account's
    /// across workspaces and applies beside this one.
    /// </remarks>
    public int ImageRunningPerWorkspace { get; set; } = 3;

    /// <summary>Generated-image variants one workspace may request in a UTC day.</summary>
    /// <remarks>
    /// A bound on the workspace whoever asks, beside the plan's daily picture cap, which is the account's across
    /// every workspace it works in (AP.4.2). A request must pass both; startup refuses a plan above this one.
    /// </remarks>
    public int ImageVariantsPerWorkspacePerDay { get; set; } = 200;

    /// <summary>Publications one workspace may confirm in a UTC day.</summary>
    public int PublicationsPerWorkspacePerDay { get; set; } = 100;

    public OperationRateRule RateFor(LimitedOperation operation) =>
        Rates.TryGetValue(operation, out var rule) ? rule : new OperationRateRule();

    /// <summary>The 15.3 table, as approved.</summary>
    public static Dictionary<LimitedOperation, OperationRateRule> DefaultRates() => new()
    {
        [LimitedOperation.AiRequest] = new()
        {
            User = TokenBucketRule.PerMinute(10, 20), Workspace = TokenBucketRule.PerMinute(30, 60),
        },
        [LimitedOperation.ImageGeneration] = new()
        {
            User = TokenBucketRule.PerMinute(3, 6), Workspace = TokenBucketRule.PerMinute(10, 20),
        },
        [LimitedOperation.Upload] = new()
        {
            User = TokenBucketRule.PerMinute(10, 30), Workspace = TokenBucketRule.PerMinute(30, 120),
        },
        [LimitedOperation.PdfExport] = new() { User = TokenBucketRule.PerMinute(5, 10) },
        [LimitedOperation.TextExport] = new() { User = TokenBucketRule.PerMinute(20, 60) },
        [LimitedOperation.Search] = new() { User = TokenBucketRule.PerMinute(30, 120) },
        [LimitedOperation.Nutrition] = new() { Workspace = TokenBucketRule.PerMinute(10, 30) },
        [LimitedOperation.WorkspaceCreate] = new()
        {
            User = new TokenBucketRule { Burst = 3, Refill = 5, Per = TimeSpan.FromHours(1) },
        },
        [LimitedOperation.PublishingCommand] = new()
        {
            User = TokenBucketRule.PerMinute(5, 10), Workspace = TokenBucketRule.PerMinute(10, 30),
        },
    };
}
