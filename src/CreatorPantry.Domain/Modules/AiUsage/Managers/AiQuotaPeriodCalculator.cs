namespace CreatorPantry.Domain.Modules.AiUsage.Managers;

/// <summary>
/// Where an allowance period starts and stops, as local calendar dates. Pure arithmetic over a date, a length
/// and an anchor — no clock, no zone, no storage.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Dates in, dates out, and the zone conversion stays outside.</strong> A period's boundaries are two
/// local midnights turned into instants by <c>ITimeZoneConverter</c>, and keeping the calendar arithmetic
/// separate from that conversion is what makes the daylight-saving behaviour <c>AccountAiQuotaPeriod</c>
/// describes testable: the local dates are the intent, and a period that is thirty days and one hour long is
/// the correct rendering of it rather than a bug in this class.
/// </para>
/// <para>
/// Deterministic by construction, which is the requirement rather than a property — USAGE-003 rules out
/// boundaries recomputed from "now", and a calculator that cannot see a clock cannot recompute one.
/// </para>
/// </remarks>
public static class AiQuotaPeriodCalculator
{
    /// <summary>
    /// The local date on which the period containing <paramref name="localDate"/> begins.
    /// </summary>
    /// <param name="localDate">A local calendar date inside the period.</param>
    /// <param name="length">How long a period runs.</param>
    /// <param name="anchor">
    /// Which day a period starts on: ignored for daily, a <see cref="DayOfWeek"/> for weekly, a day of the
    /// month between 1 and <see cref="AiUsagePolicy.MonthlyAnchorMax"/> for monthly.
    /// </param>
    public static DateOnly StartOf(DateOnly localDate, AiQuotaPeriodLength length, int anchor) =>
        length switch
        {
            AiQuotaPeriodLength.Daily => localDate,
            AiQuotaPeriodLength.Weekly => localDate.AddDays(-DaysSince(localDate.DayOfWeek, anchor)),
            AiQuotaPeriodLength.Monthly => StartOfMonthly(localDate, anchor),
            _ => throw new ArgumentOutOfRangeException(
                nameof(length), length, "A period length must be declared before a period can be opened."),
        };

    /// <summary>
    /// The local date on which the period beginning at <paramref name="start"/> ends — and on which the next
    /// one begins, since the interval is half-open.
    /// </summary>
    public static DateOnly EndOf(DateOnly start, AiQuotaPeriodLength length) =>
        length switch
        {
            AiQuotaPeriodLength.Daily => start.AddDays(1),
            AiQuotaPeriodLength.Weekly => start.AddDays(7),

            // Safe without a clamp because the anchor is capped at 28: every month has the day, so the result
            // is the same day of the next month rather than something February decides. See
            // AiQuotaPeriodLength.Monthly for why a clamping rule was rejected in favour of the cap.
            AiQuotaPeriodLength.Monthly => start.AddMonths(1),
            _ => throw new ArgumentOutOfRangeException(
                nameof(length), length, "A period length must be declared before a period can be opened."),
        };

    /// <summary>
    /// How much of a period's allowance is left to hand to the next one, before the cap is applied.
    /// </summary>
    /// <param name="allowance">What the period was opened with.</param>
    /// <param name="carriedOver">What it was handed by the period before it.</param>
    /// <param name="consumed">What its settled attempts actually charged.</param>
    /// <param name="outstanding">What is still held by reservations that have not settled.</param>
    /// <remarks>
    /// Outstanding holds count against the unused balance rather than being ignored. A reservation taken just
    /// before a boundary settles into the period it named, so treating it as unspent would hand the next period
    /// allowance the previous one is still in the middle of spending.
    /// </remarks>
    public static decimal Unused(
        decimal allowance, decimal carriedOver, decimal consumed, decimal outstanding) =>
        Math.Max(allowance + carriedOver - consumed - outstanding, 0m);

    /// <summary>
    /// What a running period has left: its spendable total, less everything settled and everything still held.
    /// </summary>
    /// <remarks>
    /// The same arithmetic as <see cref="Unused"/>, and named separately because the two answer different
    /// questions — one is what a creator may still spend today, the other is what rolls into the next period.
    /// Sharing the expression is what stops the balance a creator is shown disagreeing with the one an
    /// admission then judges them against.
    /// </remarks>
    public static decimal Remaining(
        decimal allowance, decimal carriedOver, decimal consumed, decimal reserved) =>
        Math.Max(allowance + carriedOver - consumed - reserved, 0m);

    /// <summary>Whether <paramref name="anchor"/> is a day this length can actually start on.</summary>
    public static bool IsValidAnchor(AiQuotaPeriodLength length, int anchor) =>
        length switch
        {
            AiQuotaPeriodLength.Daily => anchor == 0,
            AiQuotaPeriodLength.Weekly => anchor is >= 0 and <= 6,
            AiQuotaPeriodLength.Monthly => anchor >= 1 && anchor <= AiUsagePolicy.MonthlyAnchorMax,
            _ => false,
        };

    /// <summary>
    /// The most recent occurrence of the anchor day of the month on or before <paramref name="localDate"/>.
    /// </summary>
    private static DateOnly StartOfMonthly(DateOnly localDate, int anchor) =>
        localDate.Day >= anchor
            ? new DateOnly(localDate.Year, localDate.Month, anchor)
            : new DateOnly(localDate.Year, localDate.Month, anchor).AddMonths(-1);

    /// <summary>How many days back the most recent <paramref name="anchor"/> weekday is.</summary>
    private static int DaysSince(DayOfWeek day, int anchor) => ((int)day - anchor + 7) % 7;
}
