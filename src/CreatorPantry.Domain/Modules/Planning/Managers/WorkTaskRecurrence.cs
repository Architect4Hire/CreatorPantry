namespace CreatorPantry.Domain.Modules.Planning.Managers;

/// <summary>How often a routine comes round (13A.30). No natural-language recurrence: these two, and nothing else.</summary>
/// <remarks>Append only; never renumber.</remarks>
public enum WorkTaskRecurrenceFrequency
{
    /// <summary>Every day.</summary>
    Daily = 1,

    /// <summary>On the chosen weekdays of every week.</summary>
    Weekly = 2,
}

/// <summary>A set of weekdays, Monday first, for a weekly routine.</summary>
/// <remarks>The stored values are these bits; never renumber.</remarks>
[Flags]
public enum WorkTaskWeekdays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    All = Monday | Tuesday | Wednesday | Thursday | Friday | Saturday | Sunday,
}

/// <summary>Where a routine stands (13A.30).</summary>
/// <remarks>Append only; never renumber.</remarks>
public enum WorkTaskTemplateStatus
{
    /// <summary>Making its occurrences.</summary>
    Active = 1,

    /// <summary>Stopped by its person. Nothing is made, and nothing already made is touched.</summary>
    Paused = 2,

    /// <summary>Past its last date. Nothing more will be made.</summary>
    Ended = 3,

    /// <summary>Stopped because it could not go on as it was; <c>AttentionReason</c> says why.</summary>
    NeedsAttention = 4,
}

/// <summary>Why a routine stopped on its own (13A.30).</summary>
/// <remarks>Append only; never renumber.</remarks>
public enum WorkTaskTemplateAttention
{
    /// <summary>Its owner left the workspace or can no longer keep work in it.</summary>
    OwnerUnavailable = 1,

    /// <summary>The time zone stored with it is not one the calendar knows.</summary>
    TimeZoneInvalid = 2,
}

/// <summary>
/// Which calendar dates a routine falls on (13A.30) — deterministic, and dates only: no instant, offset or hour is
/// involved, so a week the clocks change in has the same dates as any other.
/// </summary>
public static class WorkTaskRecurrence
{
    /// <summary>
    /// How far ahead occurrences are made: today and the six days after it, in the routine's own zone, so the
    /// week a person is planning already holds its routine tasks.
    /// </summary>
    public const int LookaheadDays = 7;

    /// <summary>The last date the lookahead reaches from <paramref name="today"/>.</summary>
    public static DateOnly HorizonFrom(DateOnly today) => today.AddDays(LookaheadDays - 1);

    /// <summary>Whether <paramref name="date"/> is a day the routine comes round on, ignoring its start and end.</summary>
    public static bool Matches(DateOnly date, WorkTaskRecurrenceFrequency frequency, WorkTaskWeekdays weekdays) =>
        frequency switch
        {
            WorkTaskRecurrenceFrequency.Daily => true,
            WorkTaskRecurrenceFrequency.Weekly => (weekdays & Bit(date.DayOfWeek)) != 0,
            _ => false,
        };

    /// <summary>
    /// The first date on or after <paramref name="from"/> the routine comes round on, inside its start and end, or
    /// null when there is none — the routine has ended, or names no weekday.
    /// </summary>
    public static DateOnly? FirstOnOrAfter(
        DateOnly from,
        DateOnly startsOn,
        DateOnly? endsOn,
        WorkTaskRecurrenceFrequency frequency,
        WorkTaskWeekdays weekdays)
    {
        if (frequency == WorkTaskRecurrenceFrequency.Weekly && (weekdays & WorkTaskWeekdays.All) == 0)
        {
            return null;
        }

        var date = from < startsOn ? startsOn : from;

        // A week holds every weekday, so a match is never more than seven days off.
        for (var step = 0; step < 7; step++, date = date.AddDays(1))
        {
            if (endsOn is { } end && date > end)
            {
                return null;
            }

            if (Matches(date, frequency, weekdays))
            {
                return date;
            }
        }

        return null;
    }

    /// <summary>The weekday's bit.</summary>
    public static WorkTaskWeekdays Bit(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => WorkTaskWeekdays.Monday,
        DayOfWeek.Tuesday => WorkTaskWeekdays.Tuesday,
        DayOfWeek.Wednesday => WorkTaskWeekdays.Wednesday,
        DayOfWeek.Thursday => WorkTaskWeekdays.Thursday,
        DayOfWeek.Friday => WorkTaskWeekdays.Friday,
        DayOfWeek.Saturday => WorkTaskWeekdays.Saturday,
        _ => WorkTaskWeekdays.Sunday,
    };
}
