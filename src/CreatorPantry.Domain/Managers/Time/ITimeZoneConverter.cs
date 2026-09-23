namespace CreatorPantry.Domain.Managers.Time;

/// <summary>
/// The single time-zone conversion service. Zones are IANA identifiers supplied explicitly by the
/// caller (workspace or user setting); there is no server-local or inferred zone.
/// </summary>
public interface ITimeZoneConverter
{
    /// <summary>True when <paramref name="zoneId"/> is a known IANA zone identifier.</summary>
    bool IsValidZone(string zoneId);

    /// <summary>Converts an instant to the zone's local time. Always unambiguous.</summary>
    DateTimeOffset ToZoned(DateTimeOffset utc, string zoneId);

    /// <summary>
    /// Converts a local wall-clock time in <paramref name="zoneId"/> to UTC. A time inside a DST gap is
    /// shifted forward by the gap length; a time inside a DST overlap resolves to the earlier instant.
    /// </summary>
    LocalToUtcResult ToUtc(DateOnly date, TimeOnly time, string zoneId);

    /// <summary>The local calendar date of an instant in <paramref name="zoneId"/>.</summary>
    DateOnly LocalDate(DateTimeOffset utc, string zoneId);

    /// <summary>The local day of week of an instant in <paramref name="zoneId"/>.</summary>
    DayOfWeek LocalDayOfWeek(DateTimeOffset utc, string zoneId);
}

/// <param name="Utc">The resolved instant with a zero offset.</param>
/// <param name="Offset">The zone's UTC offset at the resolved instant.</param>
/// <param name="Resolution">How the local time was resolved.</param>
public sealed record LocalToUtcResult(DateTimeOffset Utc, TimeSpan Offset, LocalTimeResolution Resolution);

public enum LocalTimeResolution
{
    /// <summary>The local time maps to exactly one instant.</summary>
    Exact,

    /// <summary>The local time fell in a DST gap and was moved forward by the gap length.</summary>
    ShiftedForward,

    /// <summary>The local time occurred twice in a DST overlap; the earlier instant was chosen.</summary>
    AmbiguousEarlier,
}
