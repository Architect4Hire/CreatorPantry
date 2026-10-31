namespace CreatorPantry.Domain.Modules.AiUsage.Managers;

/// <summary>
/// The refusals an account's AI allowance can produce, whatever kind of work asked for it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Here rather than in the module that first needed them.</strong> Both codes shipped as
/// <c>AiProposalErrors</c> constants, which was right while completions were the only thing that spent an
/// allowance. Pictures spend the same allowance (AP.2.5), and a second pair of codes for the same two facts
/// would mean a client learning two ways to read "the month is spent". The strings are unchanged, so the
/// problem catalogue, <c>ProblemResults.RetryFor</c> and every shipped client see exactly what they saw before;
/// <c>AiProposalErrors</c> now names these.
/// </para>
/// <para>
/// A refusal carries this account's own figures and never names a workspace (USAGE-007).
/// </para>
/// </remarks>
public static class AiQuotaErrors
{
    /// <summary>
    /// The requesting account's allowance for this period will not cover the request (USAGE-006).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>429, from the <c>.exhausted</c> suffix.</strong> The remedy is to wait rather than to be granted
    /// anything, the wait has a known end, and a client that already backs off on 429 is doing the right thing.
    /// </para>
    /// <para>
    /// The edge's own rate limiter answers <c>rate_limited</c> with the same status. The two are told apart by
    /// this code, and they mean different things: one says slow down, this one says the month is spent.
    /// </para>
    /// </remarks>
    public const string Exhausted = "ai.quota.exhausted";

    /// <summary>
    /// AI access is switched off for the requesting account, whatever allowance remains.
    /// </summary>
    /// <remarks>
    /// A distinct code from <see cref="Exhausted"/> and a distinct status — 403, from the <c>.suspended</c>
    /// suffix — because there is no reset to offer and no amount of waiting that changes the answer. It carries
    /// no balance for the same reason: a remaining figure for an account that cannot spend it would read as an
    /// invitation to try again.
    /// </remarks>
    public const string Suspended = "ai.quota.suspended";

    /// <summary>
    /// The requesting account's trial has ended and it is on no plan (AP.7.1, B-54), so no AI work is admitted —
    /// pack credits included — until it chooses one.
    /// </summary>
    /// <remarks>
    /// 403, from the <c>.forbidden</c> suffix, for the reason <see cref="Suspended"/> is: no reset and no amount of
    /// waiting changes the answer, so it carries no balance — only when the trial ended.
    /// </remarks>
    public const string TrialEnded = "ai.quota.trial_ended.forbidden";

    /// <summary>
    /// The request would take the account past its trial's lifetime picture cap (AP.7.1, B-52). The trial goes on,
    /// and so does work that is not a picture.
    /// </summary>
    /// <remarks>
    /// 403 rather than the daily cap's 429: the cap is for the whole trial, so nothing resets it. Carries the cap and
    /// when the trial ends, the account's own figures and nothing else.
    /// </remarks>
    public const string TrialImagesSpent = "ai.quota.trial_images.forbidden";

    /// <summary>
    /// New pictures are not being admitted for anybody right now: the platform's daily image spend breaker has
    /// tripped (PRICE-005, AP.2.7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>503, from the <c>.unavailable</c> suffix, and <c>retry: later</c>.</strong> It is not the account's
    /// allowance, so it is neither of the codes above: a client must not read it as "your month is spent" or
    /// "you are switched off". It carries no figures and no reset time — not the ceiling, not the day's total,
    /// not when the UTC day ends — because every one of them describes other people's usage, and an operator
    /// can lift the breaker before midnight anyway.
    /// </para>
    /// <para>
    /// Not under <c>ai.quota</c>: that prefix means the requesting account's own allowance, and this is not it.
    /// </para>
    /// </remarks>
    public const string CapacityUnavailable = "ai.capacity.unavailable";
}
