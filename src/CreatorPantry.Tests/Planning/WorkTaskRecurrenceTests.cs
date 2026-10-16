using CreatorPantry.Domain.Modules.Planning.Managers;

namespace CreatorPantry.Tests.Planning;

/// <summary>
/// Which dates a routine falls on (13A.30): dates only, so these need no database, no zone and no clock — which is
/// itself the clock-change rule, because nothing here can see an hour.
/// </summary>
public sealed class WorkTaskRecurrenceTests
{
    private static readonly DateOnly Thursday = new(2038, 10, 14);

    private const WorkTaskWeekdays MondayWednesdayFriday =
        WorkTaskWeekdays.Monday | WorkTaskWeekdays.Wednesday | WorkTaskWeekdays.Friday;

    [Fact]
    public void A_daily_routine_falls_on_the_first_day_it_is_asked_from()
    {
        Assert.Equal(Thursday, WorkTaskRecurrence.FirstOnOrAfter(Thursday, Thursday.AddDays(-30), null, WorkTaskRecurrenceFrequency.Daily, WorkTaskWeekdays.None));
    }

    [Fact]
    public void A_weekly_routine_falls_on_its_next_chosen_weekday()
    {
        Assert.Equal(Thursday.AddDays(1), WorkTaskRecurrence.FirstOnOrAfter(Thursday, Thursday, null, WorkTaskRecurrenceFrequency.Weekly, MondayWednesdayFriday));
        Assert.Equal(Thursday.AddDays(4), WorkTaskRecurrence.FirstOnOrAfter(Thursday.AddDays(2), Thursday, null, WorkTaskRecurrenceFrequency.Weekly, MondayWednesdayFriday));
        Assert.Equal(Thursday.AddDays(7), WorkTaskRecurrence.FirstOnOrAfter(Thursday.AddDays(1), Thursday, null, WorkTaskRecurrenceFrequency.Weekly, WorkTaskWeekdays.Thursday));
    }

    [Fact]
    public void Nothing_falls_before_the_start_or_after_the_end()
    {
        var start = Thursday.AddDays(10);
        Assert.Equal(start, WorkTaskRecurrence.FirstOnOrAfter(Thursday, start, null, WorkTaskRecurrenceFrequency.Daily, WorkTaskWeekdays.None));

        Assert.Null(WorkTaskRecurrence.FirstOnOrAfter(Thursday, Thursday.AddDays(-5), Thursday.AddDays(-1), WorkTaskRecurrenceFrequency.Daily, WorkTaskWeekdays.None));

        // A Monday-only routine that ends on the Sunday before its next Monday has nothing left.
        Assert.Null(WorkTaskRecurrence.FirstOnOrAfter(Thursday, Thursday, Thursday.AddDays(3), WorkTaskRecurrenceFrequency.Weekly, WorkTaskWeekdays.Monday));
    }

    [Fact]
    public void A_weekly_routine_naming_no_weekday_never_falls()
    {
        Assert.Null(WorkTaskRecurrence.FirstOnOrAfter(Thursday, Thursday, null, WorkTaskRecurrenceFrequency.Weekly, WorkTaskWeekdays.None));
    }

    [Fact]
    public void The_lookahead_is_today_and_the_six_days_after_it()
    {
        Assert.Equal(Thursday.AddDays(6), WorkTaskRecurrence.HorizonFrom(Thursday));
        Assert.Equal(7, WorkTaskRecurrence.LookaheadDays);
    }

    [Theory]
    [InlineData("2038-11-01")] // the week Chicago's clocks fall back in
    [InlineData("2039-03-07")] // the week they spring forward in
    [InlineData("2011-12-26")] // the week Samoa skipped a whole day
    public void A_week_the_clocks_change_in_has_the_same_seven_dates_as_any_other(string monday)
    {
        var start = DateOnly.Parse(monday, System.Globalization.CultureInfo.InvariantCulture);
        var dates = new List<DateOnly>();

        for (var next = WorkTaskRecurrence.FirstOnOrAfter(start, start, null, WorkTaskRecurrenceFrequency.Daily, WorkTaskWeekdays.None);
             next is { } date && date <= WorkTaskRecurrence.HorizonFrom(start);
             next = WorkTaskRecurrence.FirstOnOrAfter(date.AddDays(1), start, null, WorkTaskRecurrenceFrequency.Daily, WorkTaskWeekdays.None))
        {
            dates.Add(date);
        }

        Assert.Equal(Enumerable.Range(0, 7).Select(start.AddDays), dates);
    }

    [Fact]
    public void Every_weekday_is_its_own_bit()
    {
        var bits = Enum.GetValues<DayOfWeek>().Select(WorkTaskRecurrence.Bit).ToList();

        Assert.Equal(7, bits.Distinct().Count());
        Assert.Equal(WorkTaskWeekdays.All, bits.Aggregate(WorkTaskWeekdays.None, (all, bit) => all | bit));
    }
}
