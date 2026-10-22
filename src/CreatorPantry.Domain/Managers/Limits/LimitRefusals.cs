using CreatorPantry.Domain.Managers.Results;

namespace CreatorPantry.Domain.Managers.Limits;

/// <summary>The three refusals a limit can give. Each ends in <c>.exhausted</c>, so each is a 429 with <c>retry: later</c>.</summary>
public static class LimitErrorCodes
{
    /// <summary>Too many requests too quickly; the bucket refills.</summary>
    public const string RateExhausted = "limits.rate.exhausted";

    /// <summary>Too much work already queued or running; it frees as that work finishes.</summary>
    public const string ConcurrencyExhausted = "limits.concurrency.exhausted";

    /// <summary>The workspace's allowance for the day is spent; it resets at the next UTC midnight.</summary>
    public const string BudgetExhausted = "limits.budget.exhausted";
}

/// <summary>
/// Builds the one shape every limit refusal has (15.3): what was limited, whose allowance it was, the allowance,
/// and when to come back.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Only the caller's own figures.</strong> A workspace refusal reports that workspace's allowance and
/// nothing about any other; a user refusal reports the person's own. No workspace, member or account is named, so
/// a refusal cannot be used to learn about anyone else's use.
/// </para>
/// <para>
/// <c>resetsAt</c> is a <see cref="DateTimeOffset"/> because the HTTP layer reads it back as <c>Retry-After</c>.
/// An in-progress cap has no instant to give — it frees when work finishes — so it carries none, and a client is
/// told <c>retry: later</c> without a figure that would be a guess.
/// </para>
/// </remarks>
public static class LimitRefusal
{
    public static OperationError Rate(LimitedOperation operation, LimitScope scope, int limit, DateTimeOffset resetsAt) =>
        Error(
            LimitErrorCodes.RateExhausted,
            scope is LimitScope.User
                ? "You are doing that faster than this allows. Wait a moment and try again."
                : "This workspace is doing that faster than this allows. Wait a moment and try again.",
            operation,
            scope,
            limit,
            resetsAt);

    public static OperationError Concurrency(LimitedOperation operation, LimitScope scope, int limit) =>
        Error(
            LimitErrorCodes.ConcurrencyExhausted,
            scope is LimitScope.User
                ? "You already have as much of this waiting as can be queued. Try again when some of it finishes."
                : "This workspace already has as much of this waiting as can be queued. Try again when some of it finishes.",
            operation,
            scope,
            limit,
            resetsAt: null);

    public static OperationError Budget(LimitedOperation operation, int limit, DateTimeOffset resetsAt) =>
        Error(
            LimitErrorCodes.BudgetExhausted,
            "This workspace has used today's allowance for this. It resets at midnight UTC.",
            operation,
            LimitScope.Workspace,
            limit,
            resetsAt);

    private static OperationError Error(
        string code, string message, LimitedOperation operation, LimitScope scope, int limit, DateTimeOffset? resetsAt)
    {
        var extensions = new Dictionary<string, object?>
        {
            ["operation"] = operation.ToString(),
            ["scope"] = scope.ToString(),
            ["limit"] = limit,
        };

        if (resetsAt is { } instant)
        {
            extensions["resetsAt"] = instant;
        }

        return new OperationError(code, message, new Dictionary<string, string[]>(), extensions);
    }
}

/// <summary>The UTC day every daily budget counts in.</summary>
/// <remarks>
/// UTC rather than the workspace's zone: one boundary for every workspace, nothing to resolve before counting, and
/// a reset instant a client can be told exactly.
/// </remarks>
public static class LimitDay
{
    public static DateTimeOffset StartOf(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();

        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
    }

    public static DateTimeOffset NextAfter(DateTimeOffset instant) => StartOf(instant).AddDays(1);
}

/// <summary>
/// Implemented by a request model whose one request stands for several units of work — a batch of sections is
/// that many AI operations — so a rate limit charges what the request will cost.
/// </summary>
public interface ILimitCost
{
    /// <summary>Units this request costs; zero for a request that starts no limited work at all.</summary>
    int LimitCost { get; }
}
