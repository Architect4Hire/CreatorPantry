namespace CreatorPantry.Domain.Modules.Planning.Managers;

/// <summary>What one routine's pass did.</summary>
public enum WorkTaskTemplateOutcome
{
    /// <summary>It made what was due, or found nothing due yet.</summary>
    Materialized = 0,

    /// <summary>It is no longer this pass's to work — gone, not active, or its lease lapsed to another pass.</summary>
    Skipped = 1,

    /// <summary>It reached its last date.</summary>
    Ended = 2,

    /// <summary>It stopped on its own and now needs its person.</summary>
    NeedsAttention = 3,

    /// <summary>It changed under this pass; the next pass carries on.</summary>
    Stale = 4,
}

/// <summary>One routine's pass: what happened, and how many tasks it made.</summary>
public sealed record WorkTaskTemplateRun(WorkTaskTemplateOutcome Outcome, int Made);
