namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>Bounds on what a dashboard summary of this module reads.</summary>
public static class WorkflowDashboardPolicy
{
    /// <summary>
    /// The most runs either list names, however many the reader has. Three: the dashboard answers "what next",
    /// and a fourth row is the hub's job (13A.11).
    /// </summary>
    public const int ListLimit = 3;
}

/// <summary>
/// One run as the dashboard names it (13A.33a): where to pick it up, and nothing of what it holds.
/// </summary>
/// <remarks>
/// No draft, no reason, no linked record and no route: the run page (13A.6) opens on the step the run itself is
/// on, so <see cref="Id"/> is the deep link and <see cref="StepTitle"/> is only what to call it on the way.
/// </remarks>
/// <param name="StepKey">The step it is on, or null when every step is done and only finishing is left.</param>
/// <param name="StepTitle">That step's title from the pinned version, null with <paramref name="StepKey"/>.</param>
/// <param name="YouCanDoIt">
/// Whether the reader's role may take that step's main action or one of its alternatives. False means someone
/// else's to carry on, and the dashboard offers to open it rather than to continue it.
/// </param>
/// <param name="AttentionReasons">
/// Why it needs a look — codes from <see cref="WorkflowRunAttentionReasons"/>, as the run list gives them. Empty
/// for a run that needs none.
/// </param>
public sealed record WorkflowDashboardRunServiceModel(
    Guid Id,
    string Title,
    string DefinitionKey,
    string Goal,
    string? StepKey,
    string? StepTitle,
    bool YouCanDoIt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> AttentionReasons);

/// <summary>
/// The reader's own guided workflows, for the dashboard (13A.33a, FLOW-004): what to carry on with, and what has
/// stopped.
/// </summary>
/// <remarks>
/// <strong>The reader's, not the workspace's.</strong> Both lists are keyed on the caller's membership for
/// <see cref="Continue"/> and on their role for <see cref="NeedsAttention"/>, so two members of one workspace are
/// answered differently — and rightly: a run somebody else is in the middle of is not yours to pick up.
/// </remarks>
/// <param name="InProgressCount">Every run in progress that the reader started or was handed, however many are listed.</param>
/// <param name="Continue">
/// The ones to carry on with, most recently touched first — at most <see cref="WorkflowDashboardPolicy.ListLimit"/>.
/// </param>
/// <param name="NeedsAttentionCount">
/// Every run in progress in the workspace with a reason to look at it, however many are listed. Not a subset of
/// <paramref name="InProgressCount"/>: a run somebody else started can need a look too.
/// </param>
/// <param name="NeedsAttention">The longest-waiting of them first — at most <see cref="WorkflowDashboardPolicy.ListLimit"/>.</param>
public sealed record WorkflowDashboardSummaryServiceModel(
    int InProgressCount,
    IReadOnlyList<WorkflowDashboardRunServiceModel> Continue,
    int NeedsAttentionCount,
    IReadOnlyList<WorkflowDashboardRunServiceModel> NeedsAttention);
