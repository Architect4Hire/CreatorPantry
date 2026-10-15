namespace CreatorPantry.Domain.Modules.Planning.Managers;

/// <summary>Limits and ordering constants for work tasks, shared by their configuration and their write seam.</summary>
public static class WorkTaskPolicy
{
    public const int TitleMaxLength = 200;

    public const int DescriptionMaxLength = 4000;

    /// <summary>
    /// A step key in a workflow definition. The same width the workflow contract allows, stated here rather than
    /// read from it because another module's policy does not cross a module boundary.
    /// </summary>
    public const int WorkflowStepKeyMaxLength = 64;

    /// <summary>The longest estimate one task may carry: a day. Work bigger than that is more than one task.</summary>
    public const int EstimateMaxMinutes = 24 * 60;

    /// <summary>The most labels one task may carry, the same cap an asset's tags have.</summary>
    public const int MaxLabels = 25;

    /// <summary>
    /// The distance left between two neighbouring tasks' ranks, and the rank of the first task in a group.
    /// </summary>
    /// <remarks>
    /// The content board's scheme (<c>ContentCardPolicy.RankGap</c>): a task dropped between two others takes
    /// the midpoint, so a move writes one row, and a group whose gaps run out is re-spaced in one transaction.
    /// Order within a group is <c>SortRank, Id</c>, so two tasks on the same rank still read in one order.
    /// </remarks>
    public const long RankGap = 1024;
}
