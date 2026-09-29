namespace CreatorPantry.Domain.Modules.AiUsage.Managers;

/// <summary>
/// How long one allowance period runs, in the account's own time zone.
/// </summary>
/// <remarks>
/// Paired with <c>AccountAiQuota.PeriodAnchor</c>, whose valid range depends on which member this is — see
/// <see cref="AiUsagePolicy.MonthlyAnchorMax"/> and the check constraint that enforces the pairing.
/// </remarks>
public enum AiQuotaPeriodLength
{
    /// <summary>Not declared. Never valid on a stored row; the check constraint refuses it.</summary>
    Unspecified = 0,

    /// <summary>One local day. The anchor is unused and must be zero.</summary>
    Daily = 1,

    /// <summary>One local week. The anchor is the <see cref="DayOfWeek"/> the period starts on.</summary>
    Weekly = 2,

    /// <summary>One local month. The anchor is the day of the month the period starts on.</summary>
    /// <remarks>
    /// Capped at <see cref="AiUsagePolicy.MonthlyAnchorMax"/> so that every month actually has the anchor day.
    /// An anchor of 31 would need a clamp-to-month-end rule that behaves differently in February than in
    /// March, and a period boundary that moves depending on the month is exactly the non-determinism
    /// USAGE-003 rules out.
    /// </remarks>
    Monthly = 3,
}
