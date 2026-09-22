using CreatorPantry.Domain.Time;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Time;

public class TimeZoneConverterTests
{
    private const string Chicago = "America/Chicago";

    private readonly ITimeZoneConverter _converter =
        new ServiceCollection().AddApplicationTime().BuildServiceProvider().GetRequiredService<ITimeZoneConverter>();

    [Fact]
    public void Ordinary_local_time_is_exact()
    {
        var result = _converter.ToUtc(new DateOnly(2026, 7, 1), new TimeOnly(12, 0), Chicago);

        Assert.Equal(Utc(2026, 7, 1, 17, 0), result.Utc);
        Assert.Equal(TimeSpan.FromHours(-5), result.Offset);
        Assert.Equal(LocalTimeResolution.Exact, result.Resolution);
    }

    [Fact]
    public void Spring_forward_gap_shifts_forward_by_the_gap()
    {
        // 2026-03-08 02:30 does not exist in Chicago; clocks jump from 02:00 CST to 03:00 CDT.
        var result = _converter.ToUtc(new DateOnly(2026, 3, 8), new TimeOnly(2, 30), Chicago);

        Assert.Equal(Utc(2026, 3, 8, 8, 30), result.Utc); // 03:30 CDT
        Assert.Equal(TimeSpan.FromHours(-5), result.Offset);
        Assert.Equal(LocalTimeResolution.ShiftedForward, result.Resolution);
    }

    [Fact]
    public void Fall_back_overlap_resolves_to_the_earlier_instant()
    {
        // 2026-11-01 01:30 occurs twice in Chicago: first in CDT (-05:00), then in CST (-06:00).
        var result = _converter.ToUtc(new DateOnly(2026, 11, 1), new TimeOnly(1, 30), Chicago);

        Assert.Equal(Utc(2026, 11, 1, 6, 30), result.Utc);
        Assert.Equal(TimeSpan.FromHours(-5), result.Offset);
        Assert.Equal(LocalTimeResolution.AmbiguousEarlier, result.Resolution);
    }

    [Fact]
    public void Southern_hemisphere_overlap_resolves_to_the_earlier_instant()
    {
        // Sydney leaves daylight time on 2026-04-05: 03:00 AEDT becomes 02:00 AEST.
        var result = _converter.ToUtc(new DateOnly(2026, 4, 5), new TimeOnly(2, 30), "Australia/Sydney");

        Assert.Equal(Utc(2026, 4, 4, 15, 30), result.Utc);
        Assert.Equal(TimeSpan.FromHours(11), result.Offset);
        Assert.Equal(LocalTimeResolution.AmbiguousEarlier, result.Resolution);
    }

    [Fact]
    public void Half_hour_dst_gap_shifts_forward_by_thirty_minutes()
    {
        // Lord Howe Island moves from +10:30 to +11:00 at 02:00 on 2026-10-04.
        var result = _converter.ToUtc(new DateOnly(2026, 10, 4), new TimeOnly(2, 15), "Australia/Lord_Howe");

        Assert.Equal(Utc(2026, 10, 3, 15, 45), result.Utc); // 02:45 +11:00
        Assert.Equal(TimeSpan.FromHours(11), result.Offset);
        Assert.Equal(LocalTimeResolution.ShiftedForward, result.Resolution);
    }

    [Fact]
    public void Zone_without_dst_is_exact_on_another_zones_transition_date()
    {
        var result = _converter.ToUtc(new DateOnly(2026, 3, 8), new TimeOnly(2, 30), "Asia/Kolkata");

        Assert.Equal(Utc(2026, 3, 7, 21, 0), result.Utc);
        Assert.Equal(new TimeSpan(5, 30, 0), result.Offset);
        Assert.Equal(LocalTimeResolution.Exact, result.Resolution);
    }

    [Fact]
    public void Utc_to_zoned_uses_the_offset_in_effect()
    {
        var zoned = _converter.ToZoned(Utc(2026, 3, 8, 8, 30), Chicago);

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 3, 30, 0, TimeSpan.FromHours(-5)), zoned);
    }

    [Fact]
    public void Day_of_week_comes_from_the_local_date_not_the_utc_date()
    {
        var instant = Utc(2026, 9, 22, 3, 0); // Tuesday in UTC, Monday 22:00 CDT in Chicago

        Assert.Equal(DayOfWeek.Tuesday, instant.UtcDateTime.DayOfWeek);
        Assert.Equal(new DateOnly(2026, 9, 21), _converter.LocalDate(instant, Chicago));
        Assert.Equal(DayOfWeek.Monday, _converter.LocalDayOfWeek(instant, Chicago));
    }

    [Theory]
    [InlineData("America/Chicago", true)]
    [InlineData("UTC", true)]
    [InlineData("Central Standard Time", false)] // Windows id, not IANA
    [InlineData("Mars/Olympus_Mons", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void Only_iana_zone_ids_are_valid(string zoneId, bool expected) =>
        Assert.Equal(expected, _converter.IsValidZone(zoneId));

    [Fact]
    public void Unknown_zone_is_rejected_rather_than_defaulted()
    {
        Assert.Throws<InvalidTimeZoneException>(() =>
            _converter.ToUtc(new DateOnly(2026, 7, 1), new TimeOnly(12, 0), "Central Standard Time"));
        Assert.Throws<InvalidTimeZoneException>(() =>
            _converter.ToZoned(Utc(2026, 7, 1, 12, 0), ""));
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}
