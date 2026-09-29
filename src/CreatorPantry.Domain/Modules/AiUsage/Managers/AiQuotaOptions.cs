using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.AiUsage.Managers;

/// <summary>
/// The deployment's allowance settings: the quota an account gets when nobody set it one, what a run of each
/// capability is estimated at, and how reported tokens become the unit a period is denominated in.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is where USAGE-003's platform default actually lives.</strong> <c>AccountAiQuota.Allowance</c>
/// is nullable precisely so domain code has nothing to fall back from; the number comes from here, and it stops
/// being a default the moment a period opens and copies it onto <c>AccountAiQuotaPeriod.Allowance</c>.
/// </para>
/// <para>
/// Every number here is read by domain code, never by a model. A model never participates in an admission
/// decision and never sees a balance (ai.md).
/// </para>
/// </remarks>
public sealed class AiQuotaOptions
{
    public const string SectionName = "Ai:Quota";

    /// <summary>The allowance per period for an account with no quota row of its own.</summary>
    public decimal DefaultAllowance { get; set; } = 1000m;

    /// <inheritdoc cref="DefaultAllowance"/>
    public AiQuotaUnit DefaultUnit { get; set; } = AiQuotaUnit.Credits;

    /// <inheritdoc cref="DefaultAllowance"/>
    public AiQuotaPeriodLength DefaultPeriodLength { get; set; } = AiQuotaPeriodLength.Monthly;

    /// <summary>
    /// Which local day a default period starts on, in the same terms <c>AccountAiQuota.PeriodAnchor</c> uses:
    /// unused and zero for daily, a <see cref="DayOfWeek"/> for weekly, a day of the month up to
    /// <see cref="AiUsagePolicy.MonthlyAnchorMax"/> for monthly.
    /// </summary>
    public int DefaultPeriodAnchor { get; set; } = 1;

    /// <summary>
    /// The zone the default period's boundaries are computed in.
    /// </summary>
    /// <remarks>
    /// An IANA identifier, validated before a period is opened with it. It is the platform's calendar, not the
    /// server's: an account that wants its own says so with a quota row.
    /// </remarks>
    public string DefaultTimeZoneId { get; set; } = "Etc/UTC";

    /// <inheritdoc cref="DefaultAllowance"/>
    public AiQuotaCarryOver DefaultCarryOver { get; set; } = AiQuotaCarryOver.None;

    /// <summary>
    /// The default carry-over cap. Required when <see cref="DefaultCarryOver"/> carries unused allowance
    /// forward, and ignored otherwise — the same pairing the quota table constrains.
    /// </summary>
    public decimal? DefaultCarryOverCap { get; set; }

    /// <summary>
    /// What one provider call of a given capability is estimated at, in <see cref="AiQuotaUnit.Credits"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A configured number, never a learned one.</strong> A trailing average of the account's own
    /// history would make two identical requests answerable differently, would make admission depend on
    /// unrelated past traffic, and would make a competing-worker result irreproducible. This is auditable and
    /// the same for everyone.
    /// </para>
    /// <para>
    /// A capability absent from here is estimated at <see cref="DefaultTaskEstimate"/>. There is deliberately
    /// no fallback below that: see its remarks.
    /// </para>
    /// </remarks>
    public Dictionary<AiTaskType, decimal> TaskEstimates { get; } = [];

    /// <summary>
    /// What a capability not listed in <see cref="TaskEstimates"/> is estimated at, per provider call.
    /// </summary>
    /// <remarks>
    /// Must be above zero. A zero default would make an unpriced capability unmetered, which is the failure
    /// <c>ConfiguredAiCostEstimator</c> avoids by returning null rather than zero for a model it has no price
    /// for: an unknown cost is not a free one. Admission refuses rather than reserving nothing.
    /// </remarks>
    public decimal DefaultTaskEstimate { get; set; } = 10m;

    /// <summary>
    /// How many provider calls one admitted run is estimated to be permitted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A run may legitimately call a provider more than once — a corrective re-ask after a schema failure is
    /// the ordinary case — so the hold taken at admission is this many times the per-call estimate. Admitting
    /// for one call while permitting several is admitting past the allowance by construction.
    /// </para>
    /// <para>
    /// <strong>It restates <c>Ai:Gateway:MaxSchemaCorrections</c> plus one rather than reading it.</strong>
    /// The gateway's options live in another module's namespace this one may not reach into, which is the same
    /// trade <see cref="AiUsagePolicy"/> already documents for the provider-identifier widths. Configure the
    /// two together: a value below <c>MaxSchemaCorrections + 1</c> under-reserves every corrected run, which
    /// settlement will record honestly and admission will have let through.
    /// </para>
    /// </remarks>
    public int EstimatedCallsPerRun { get; set; } = 2;

    /// <summary>
    /// Credits per million reported tokens, by model name.
    /// </summary>
    /// <remarks>
    /// The weighting that makes <see cref="AiQuotaUnit.Credits"/> mean the same thing whichever model served a
    /// request — output tokens cost several times input tokens, and one model costs an order of magnitude more
    /// than another, neither of which a creator chooses or sees. A model absent from here cannot be converted,
    /// and settlement charges the estimate rather than nothing.
    /// </remarks>
    public Dictionary<string, AiQuotaModelRate> ModelRates { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How many tokens one credit represents, for a period denominated in
    /// <see cref="AiQuotaUnit.Tokens"/>.
    /// </summary>
    /// <remarks>
    /// The bridge that keeps the declared-but-unused token unit coherent: the estimates above are written in
    /// credits, and a token-denominated period needs them in tokens. Reported usage needs no bridge there — it
    /// is already tokens.
    /// </remarks>
    public decimal TokensPerCredit { get; set; } = 1000m;
}

/// <param name="CreditsPerMillionInputTokens">Credits charged per million reported input tokens.</param>
/// <param name="CreditsPerMillionOutputTokens">Credits charged per million reported output tokens.</param>
public sealed record AiQuotaModelRate(
    decimal CreditsPerMillionInputTokens,
    decimal CreditsPerMillionOutputTokens);
