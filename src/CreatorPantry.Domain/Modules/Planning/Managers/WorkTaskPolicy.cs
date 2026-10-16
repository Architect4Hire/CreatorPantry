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

    /// <summary>An IANA zone identifier such as <c>America/Chicago</c>, as a routine stores it (13A.30).</summary>
    public const int TimeZoneIdMaxLength = 64;

    /// <summary>The longest estimate one task may carry: a day. Work bigger than that is more than one task.</summary>
    public const int EstimateMaxMinutes = 24 * 60;

    /// <summary>The most labels one task may carry, the same cap an asset's tags have.</summary>
    public const int MaxLabels = 25;

    /// <summary>
    /// The most rows one group of the daily view returns (13A.25), whatever the day holds.
    /// </summary>
    /// <remarks>
    /// A day's work is bounded by what a person can do, so a group past this is a sign of something other
    /// than a plan — an import, a runaway template — and paging it would dignify that rather than show it.
    /// The group says it was cut; it does not say by how much, because a count nobody is showing is a second
    /// query per group for nothing.
    /// </remarks>
    public const int TodayGroupMaxRows = 50;

    /// <summary>How many unplanned tasks the daily view may offer as suggestions (13A.25).</summary>
    /// <remarks>
    /// Three, because a suggestion is a nudge and a list of them is another backlog. They are offered only
    /// when asked for, and offering one schedules nothing.
    /// </remarks>
    public const int TodaySuggestionCount = 3;

    /// <summary>How many tasks a person may choose as their top three for one day (13A.32). Three.</summary>
    public const int TopThreeMax = 3;

    /// <summary>The most rows one group of the weekly view returns, and the most one day of it does (13A.26).</summary>
    /// <remarks>
    /// The daily view's cap and for its reason. Unlike the daily view, every group of the week also carries
    /// totals over the whole group, so a cut group still says how much it holds.
    /// </remarks>
    public const int WeekGroupMaxRows = 50;

    /// <summary>
    /// The distance left between two neighbouring tasks' ranks, and the rank of the first task in a group.
    /// </summary>
    /// <remarks>
    /// The content board's scheme (<c>ContentCardPolicy.RankGap</c>): a task dropped between two others takes
    /// the midpoint, so a move writes one row, and a group whose gaps run out is re-spaced in one transaction.
    /// Order within a group is <c>SortRank, Id</c>, so two tasks on the same rank still read in one order.
    /// </remarks>
    public const long RankGap = 1024;

    /// <summary>
    /// A workflow run's address in the app, relative to the workspace, which a task links back to (13A.29). The
    /// same shape the workflow hub opens a run at; never absolute, never a scheme.
    /// </summary>
    public static string WorkflowRunRoute(Guid runId) => $"workflows/runs/{runId:D}";
}
