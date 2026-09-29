using CreatorPantry.Domain.Modules.AiUsage.Managers;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// Where an allowance period starts and stops (USAGE-003/004). Pure arithmetic over local dates, so these
/// need no database and no clock — which is the point of keeping the calendar rule separate from the zone
/// conversion that renders it.
/// </summary>
public sealed class AiQuotaPeriodCalculatorTests
{
    // ---- daily -----------------------------------------------------------------------------------------

    [Fact]
    public void A_daily_period_is_the_local_day_it_falls_in()
    {
        var day = new DateOnly(2026, 9, 29);

        Assert.Equal(day, AiQuotaPeriodCalculator.StartOf(day, AiQuotaPeriodLength.Daily, 0));
        Assert.Equal(
            new DateOnly(2026, 9, 30), AiQuotaPeriodCalculator.EndOf(day, AiQuotaPeriodLength.Daily));
    }

    // ---- weekly ----------------------------------------------------------------------------------------

    /// <summary>
    /// 2026-09-29 is a Tuesday. Anchored to Monday it belongs to the week beginning the 28th; anchored to
    /// Wednesday it belongs to the one beginning the 23rd, which is the case a "round down to the anchor"
    /// rule gets wrong by looking forward.
    /// </summary>
    [Theory]
    [InlineData(DayOfWeek.Monday, 2026, 9, 28)]
    [InlineData(DayOfWeek.Tuesday, 2026, 9, 29)]
    [InlineData(DayOfWeek.Wednesday, 2026, 9, 23)]
    [InlineData(DayOfWeek.Sunday, 2026, 9, 27)]
    public void A_weekly_period_starts_on_the_most_recent_anchor_weekday(
        DayOfWeek anchor, int year, int month, int day)
    {
        var start = AiQuotaPeriodCalculator.StartOf(
            new DateOnly(2026, 9, 29), AiQuotaPeriodLength.Weekly, (int)anchor);

        Assert.Equal(new DateOnly(year, month, day), start);
        Assert.Equal(anchor, start.DayOfWeek);
        Assert.Equal(start.AddDays(7), AiQuotaPeriodCalculator.EndOf(start, AiQuotaPeriodLength.Weekly));
    }

    // ---- monthly ---------------------------------------------------------------------------------------

    [Fact]
    public void A_monthly_period_starts_on_the_anchor_day_of_this_month_once_it_has_passed()
    {
        var start = AiQuotaPeriodCalculator.StartOf(
            new DateOnly(2026, 9, 29), AiQuotaPeriodLength.Monthly, 15);

        Assert.Equal(new DateOnly(2026, 9, 15), start);
        Assert.Equal(
            new DateOnly(2026, 10, 15), AiQuotaPeriodCalculator.EndOf(start, AiQuotaPeriodLength.Monthly));
    }

    [Fact]
    public void A_monthly_period_reaches_back_into_the_previous_month_before_the_anchor_day()
    {
        var start = AiQuotaPeriodCalculator.StartOf(
            new DateOnly(2026, 9, 3), AiQuotaPeriodLength.Monthly, 15);

        Assert.Equal(new DateOnly(2026, 8, 15), start);
    }

    /// <summary>
    /// The reason the anchor is capped at 28 rather than clamped: every month has the day, so a period ending
    /// in February ends on the same day as one ending in March. A clamping rule would move the boundary
    /// depending on the month, which is the non-determinism USAGE-003 rules out.
    /// </summary>
    [Fact]
    public void A_monthly_period_anchored_at_the_cap_lands_on_the_same_day_in_february()
    {
        var start = AiQuotaPeriodCalculator.StartOf(
            new DateOnly(2027, 1, 30), AiQuotaPeriodLength.Monthly, AiUsagePolicy.MonthlyAnchorMax);

        Assert.Equal(new DateOnly(2027, 1, 28), start);
        Assert.Equal(
            new DateOnly(2027, 2, 28), AiQuotaPeriodCalculator.EndOf(start, AiQuotaPeriodLength.Monthly));
    }

    /// <summary>
    /// Consecutive periods abut exactly: one ends where the next begins, with no gap and no overlap. That is
    /// what makes the half-open interval a partition of the calendar rather than a set of windows that happen
    /// to be near each other.
    /// </summary>
    [Theory]
    [InlineData(AiQuotaPeriodLength.Daily, 0)]
    [InlineData(AiQuotaPeriodLength.Weekly, (int)DayOfWeek.Monday)]
    [InlineData(AiQuotaPeriodLength.Monthly, 15)]
    public void Consecutive_periods_abut(AiQuotaPeriodLength length, int anchor)
    {
        var start = AiQuotaPeriodCalculator.StartOf(new DateOnly(2026, 9, 29), length, anchor);

        for (var i = 0; i < 14; i++)
        {
            var end = AiQuotaPeriodCalculator.EndOf(start, length);

            Assert.True(end > start);
            Assert.Equal(start, AiQuotaPeriodCalculator.StartOf(end.AddDays(-1), length, anchor));
            Assert.Equal(end, AiQuotaPeriodCalculator.StartOf(end, length, anchor));

            start = end;
        }
    }

    // ---- unused ----------------------------------------------------------------------------------------

    [Fact]
    public void Unused_allowance_is_what_is_left_after_everything_spent_and_everything_held()
    {
        Assert.Equal(25m, AiQuotaPeriodCalculator.Unused(100m, 10m, 60m, 25m));
    }

    /// <summary>
    /// A hold taken just before a boundary settles into the period it named, so counting it as unused would
    /// hand the next period allowance the previous one is still in the middle of spending.
    /// </summary>
    [Fact]
    public void An_outstanding_hold_is_not_unused_allowance()
    {
        Assert.Equal(0m, AiQuotaPeriodCalculator.Unused(100m, 0m, 0m, 100m));
    }

    /// <summary>An overspent period hands nothing forward, and never a negative.</summary>
    [Fact]
    public void An_overspent_period_carries_nothing_rather_than_a_debt()
    {
        Assert.Equal(0m, AiQuotaPeriodCalculator.Unused(100m, 0m, 140m, 0m));
    }

    // ---- anchors ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(AiQuotaPeriodLength.Daily, 0, true)]
    [InlineData(AiQuotaPeriodLength.Daily, 3, false)]
    [InlineData(AiQuotaPeriodLength.Weekly, 6, true)]
    [InlineData(AiQuotaPeriodLength.Weekly, 7, false)]
    [InlineData(AiQuotaPeriodLength.Monthly, 1, true)]
    [InlineData(AiQuotaPeriodLength.Monthly, 28, true)]
    [InlineData(AiQuotaPeriodLength.Monthly, 29, false)]
    [InlineData(AiQuotaPeriodLength.Monthly, 0, false)]
    [InlineData(AiQuotaPeriodLength.Unspecified, 0, false)]
    public void An_anchor_is_valid_only_for_the_length_that_can_start_on_it(
        AiQuotaPeriodLength length, int anchor, bool valid) =>
        Assert.Equal(valid, AiQuotaPeriodCalculator.IsValidAnchor(length, anchor));

    [Fact]
    public void An_undeclared_length_has_no_boundaries_at_all()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AiQuotaPeriodCalculator.StartOf(new DateOnly(2026, 9, 29), AiQuotaPeriodLength.Unspecified, 0));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AiQuotaPeriodCalculator.EndOf(new DateOnly(2026, 9, 29), AiQuotaPeriodLength.Unspecified));
    }
}
