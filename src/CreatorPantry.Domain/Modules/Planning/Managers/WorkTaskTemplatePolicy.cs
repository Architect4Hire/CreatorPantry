namespace CreatorPantry.Domain.Modules.Planning.Managers;

/// <summary>How the worker makes routines' occurrences (13A.30).</summary>
public static class WorkTaskTemplatePolicy
{
    /// <summary>
    /// How often the worker looks for due routines. Occurrences are made a week ahead, so a quarter of an hour is
    /// far more often than any routine needs; it is how soon a new or resumed one gets its first week.
    /// </summary>
    public static readonly TimeSpan PollingInterval = TimeSpan.FromMinutes(15);

    /// <summary>How long a pass holds a routine. Making a week of occurrences takes a fraction of this.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>The most routines one pass claims.</summary>
    public const int ClaimBatchSize = 100;
}
