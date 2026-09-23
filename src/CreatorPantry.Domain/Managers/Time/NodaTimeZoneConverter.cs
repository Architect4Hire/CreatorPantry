using NodaTime;
using NodaTime.TimeZones;

namespace CreatorPantry.Domain.Managers.Time;

// NodaTime's bundled TZDB keeps results identical across Windows development and Linux containers.
// NodaTime types stay inside this class; callers see BCL types only.
internal sealed class NodaTimeZoneConverter : ITimeZoneConverter
{
    private static readonly IDateTimeZoneProvider Zones = DateTimeZoneProviders.Tzdb;

    private static readonly ZoneLocalMappingResolver Resolver =
        Resolvers.CreateMappingResolver(Resolvers.ReturnEarlier, Resolvers.ReturnForwardShifted);

    public bool IsValidZone(string zoneId) =>
        !string.IsNullOrWhiteSpace(zoneId) && Zones.GetZoneOrNull(zoneId) is not null;

    public DateTimeOffset ToZoned(DateTimeOffset utc, string zoneId) =>
        Instant.FromDateTimeOffset(utc).InZone(GetZone(zoneId)).ToDateTimeOffset();

    public LocalToUtcResult ToUtc(DateOnly date, TimeOnly time, string zoneId)
    {
        var mapping = GetZone(zoneId).MapLocal(NodaTime.LocalDate.FromDateOnly(date).At(LocalTime.FromTimeOnly(time)));
        var zoned = Resolver(mapping);

        var resolution = mapping.Count switch
        {
            0 => LocalTimeResolution.ShiftedForward,
            1 => LocalTimeResolution.Exact,
            _ => LocalTimeResolution.AmbiguousEarlier,
        };

        return new LocalToUtcResult(zoned.ToInstant().ToDateTimeOffset(), zoned.Offset.ToTimeSpan(), resolution);
    }

    public DateOnly LocalDate(DateTimeOffset utc, string zoneId) =>
        Instant.FromDateTimeOffset(utc).InZone(GetZone(zoneId)).Date.ToDateOnly();

    public DayOfWeek LocalDayOfWeek(DateTimeOffset utc, string zoneId) =>
        LocalDate(utc, zoneId).DayOfWeek;

    private static DateTimeZone GetZone(string zoneId) =>
        (string.IsNullOrWhiteSpace(zoneId) ? null : Zones.GetZoneOrNull(zoneId))
        ?? throw new InvalidTimeZoneException($"'{zoneId}' is not a known IANA time zone.");
}
