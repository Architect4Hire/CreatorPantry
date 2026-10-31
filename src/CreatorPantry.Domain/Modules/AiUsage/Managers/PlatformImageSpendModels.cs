using System.Globalization;
using CreatorPantry.Domain.Managers.Audit;

namespace CreatorPantry.Domain.Modules.AiUsage.Managers;

/// <summary>Where the platform's image spend breaker stands for a day (AP.2.7).</summary>
public enum PlatformImageSpendState
{
    /// <summary>No ceiling is configured, so nothing is ever refused for spend. Development only.</summary>
    Off = 1,

    /// <summary>Under the ceiling and nobody refused yet.</summary>
    Armed = 2,

    /// <summary>A request was refused for the ceiling today, and it has not been lifted.</summary>
    Tripped = 3,

    /// <summary>An operator lifted the breaker for the rest of this UTC day.</summary>
    Lifted = 4,
}

/// <summary>
/// The platform's estimated image spend for one UTC day, for an operator (AP.2.7).
/// </summary>
/// <param name="Day">The UTC calendar day.</param>
/// <param name="Currency">Always <c>USD</c>: the figures are provider list prices, not credits.</param>
/// <param name="Ceiling">The configured ceiling, read live; null when the breaker is off.</param>
/// <param name="Admitted">The list-price estimate of every picture admitted that day.</param>
/// <param name="Remaining">How much of the ceiling is left, never below nought; null when there is none.</param>
/// <param name="State">Whether new requests are being admitted against it.</param>
/// <param name="TrippedAt">When the first request was refused for it, if one was.</param>
/// <param name="LiftedAt">When an operator lifted it, if one did.</param>
/// <param name="LiftedBy">The ops client that lifted it.</param>
/// <remarks>
/// <strong>A number about everybody, and only ever shown to an operator.</strong> No creator surface reads it:
/// a creator refused for it is told that pictures cannot be made right now and nothing else (USAGE-007's
/// own-figures-only rule, applied to a figure that is nobody's own).
/// </remarks>
public sealed record PlatformImageSpendDayServiceModel(
    DateOnly Day,
    string Currency,
    decimal? Ceiling,
    decimal Admitted,
    decimal? Remaining,
    PlatformImageSpendState State,
    DateTimeOffset? TrippedAt,
    DateTimeOffset? LiftedAt,
    string? LiftedBy);

/// <summary>What a lift did, so a caller can tell a lift from a repeat.</summary>
public enum PlatformImageSpendLiftOutcome
{
    /// <summary>The day was already lifted. Nothing was written and no audit event was raised.</summary>
    Unchanged = 1,

    /// <summary>The breaker is lifted for the rest of the UTC day, and the audit row says who and why.</summary>
    Lifted = 2,
}

/// <summary>The result of lifting the breaker, with the day as it now stands.</summary>
public sealed record PlatformImageSpendLiftServiceModel(
    PlatformImageSpendLiftOutcome Outcome,
    PlatformImageSpendDayServiceModel Day);

/// <summary>Audit action codes for the image spend breaker and the subject they are written against. Append-only.</summary>
public static class PlatformImageSpendAuditActions
{
    public const string SubjectType = "PlatformImageSpendDay";

    public const string Tripped = "platform.image_spend.tripped";
    public const string Lifted = "platform.image_spend.lifted";
}

/// <summary>The arithmetic and the constants the breaker is counted in, in one place.</summary>
/// <remarks>
/// Deterministic domain code, like every other quota figure (ai.md). Whole micro-dollars because the counter
/// is added to in SQL, where SQLite cannot add a decimal; see <c>PlatformImageSpendDay</c>.
/// </remarks>
public static class PlatformImageSpendPolicy
{
    public const string Currency = "USD";

    private const decimal MicrosPerDollar = 1_000_000m;

    /// <summary>
    /// Who trips the breaker: the platform, on the operator's own rule. Never an ops client's identity.
    /// </summary>
    public static readonly PlatformActor Breaker = new(
        PlatformAuditActorType.System, "creatorpantry.image-spend-breaker", "Image spend breaker");

    /// <summary>The reason the trip's audit row records. Required on every row, and this row has no operator to give one.</summary>
    public const string TripReason = "The day's estimated image spend reached its configured ceiling.";

    /// <summary>The UTC calendar day an instant falls in, which is the counter's key.</summary>
    public static DateOnly DayOf(DateTimeOffset instant) => DateOnly.FromDateTime(instant.UtcDateTime);

    /// <summary>
    /// Dollars to whole micro-dollars, rounded up: a fraction of a micro-dollar is counted, not dropped, so the
    /// counter can only err high.
    /// </summary>
    public static long ToMicros(decimal dollars) => (long)decimal.Ceiling(dollars * MicrosPerDollar);

    public static decimal ToDollars(long micros) => micros / MicrosPerDollar;

    /// <summary>The compact state pointer an audit row carries. Figures and a state, never prose.</summary>
    public static string AuditReference(PlatformImageSpendDayServiceModel day) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"state={day.State};admitted={day.Admitted};ceiling={(day.Ceiling is { } ceiling ? ceiling.ToString(CultureInfo.InvariantCulture) : "none")}");
}
