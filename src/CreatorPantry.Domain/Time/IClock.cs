namespace CreatorPantry.Domain.Time;

/// <summary>
/// The single application clock. Exposes UTC only; local times are derived through
/// <see cref="ITimeZoneConverter"/> with an explicit IANA zone.
/// </summary>
public interface IClock
{
    /// <summary>The current instant with a zero offset.</summary>
    DateTimeOffset UtcNow { get; }
}
