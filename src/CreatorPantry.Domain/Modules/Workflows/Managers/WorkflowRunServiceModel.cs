using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>
/// One run as the creator sees it: where it stands, what it has linked, and the one thing to do next (FLOW-002).
/// </summary>
/// <remarks>
/// State and pointers only — the steps' titles come from the pinned definition, and the records the run touched
/// are named by kind and id, never copied. The same shape answers a start (13A.5) and a read (13A.6).
/// </remarks>
public sealed class WorkflowRunServiceModel
{
    public required Guid Id { get; init; }

    public required string DefinitionKey { get; init; }

    public required int DefinitionVersion { get; init; }

    /// <summary>The journey's plain-language goal, from the pinned version.</summary>
    public required string Goal { get; init; }

    public required string Title { get; init; }

    public required WorkflowRunStatus Status { get; init; }

    public required int StepCount { get; init; }

    public required int CompletedStepCount { get; init; }

    public required int SkippedStepCount { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    // Optional rather than required: a stored replay (up to 24 hours) is read back into this type, and a required
    // member it was written without would fail that read. Later additions follow the same rule.

    /// <summary>When every required step was done and the run was completed (13A.10a), or null.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>When the creator stopped this run (13A.10b), or null.</summary>
    public DateTimeOffset? AbandonedAt { get; init; }

    /// <summary>Where the creator last was in it, relative to the workspace, or null before they have left it once.</summary>
    public string? LastRoute { get; init; }

    /// <summary>The run that replaced this one when the creator started over, or null.</summary>
    public Guid? ReplacedByRunId { get; init; }

    /// <summary>What the next change to this run must quote back.</summary>
    public required string ConcurrencyToken { get; init; }

    public required IReadOnlyList<WorkflowStepRunServiceModel> Steps { get; init; }

    public required IReadOnlyList<WorkflowRunLinkServiceModel> Links { get; init; }

    /// <summary>The one recommended thing to do now, or null for a run with nothing left to do.</summary>
    public required WorkflowNextActionServiceModel? NextAction { get; init; }

    /// <summary>
    /// Where "Continue" takes the creator: where they last were, unless that names a record that is gone; else the
    /// first of the next step's actions that exists and has an address. Null for a run that is finished, or whose
    /// next step has no such address — it needs a record the run does not hold.
    /// </summary>
    public string? ResumeRoute { get; init; }

    /// <summary>
    /// True while the run is in progress and every required step is done: there is nothing left that must be done, and
    /// the run can be finished. Finishing is its own command (13A.10a); nothing finishes a run by itself.
    /// </summary>
    public bool ReadyToComplete { get; init; }

    /// <summary>Things worth knowing that do not stop the run: a record gone, a newer version, a step for someone else.</summary>
    public IReadOnlyList<WorkflowRunWarningServiceModel> Warnings { get; init; } = [];
}

/// <summary>Something about a run worth saying that does not stop it.</summary>
public sealed class WorkflowRunWarningServiceModel
{
    /// <summary>
    /// What kind of thing: <c>link_missing</c>, <c>draft_missing</c>, <c>newer_version</c> or
    /// <c>next_step_needs_role</c>. An open set — a client shows <see cref="Message"/> for a code it does not know.
    /// </summary>
    public required string Code { get; init; }

    public required string Message { get; init; }

    /// <summary>For <c>link_missing</c> and <c>draft_missing</c>: the kind of record that cannot be found.</summary>
    public WorkflowResourceKind? Kind { get; init; }

    /// <summary>For <c>link_missing</c> and <c>draft_missing</c>: the id the run holds for it.</summary>
    public Guid? ResourceId { get; init; }

    /// <summary>
    /// For <c>link_missing</c>, the step that linked it, or null for the record the run started from; for
    /// <c>draft_missing</c>, the step whose draft it was.
    /// </summary>
    public string? StepKey { get; init; }

    /// <summary>For <c>newer_version</c>: the newest version, which a new run would start on.</summary>
    public int? LatestVersion { get; init; }
}

/// <summary>The stable codes of <see cref="WorkflowRunWarningServiceModel"/>.</summary>
public static class WorkflowRunWarningCodes
{
    public const string LinkMissing = "link_missing";

    /// <summary>A step's saved draft points at a record that can no longer be found.</summary>
    public const string DraftMissing = "draft_missing";

    public const string NewerVersion = "newer_version";

    public const string NextStepNeedsRole = "next_step_needs_role";
}

public sealed class WorkflowStepRunServiceModel
{
    public required string Key { get; init; }

    public required string Title { get; init; }

    public required WorkflowStepRequirement Requirement { get; init; }

    public required WorkflowStepRunStatus Status { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public DateTimeOffset? SkippedAt { get; init; }

    /// <summary>Why the creator passed it over, in their words, when they said.</summary>
    public string? SkipReason { get; init; }

    /// <summary>The kind of record the step's unfinished work is kept in, or null when none has been saved.</summary>
    public WorkflowResourceKind? DraftKind { get; init; }

    public Guid? DraftResourceId { get; init; }

    /// <summary>When the draft was last saved — what "Saved just now" is read from.</summary>
    public DateTimeOffset? DraftSavedAt { get; init; }
}

public sealed class WorkflowRunLinkServiceModel
{
    public required WorkflowResourceKind Kind { get; init; }

    public required Guid ResourceId { get; init; }

    /// <summary>The step that linked it, or null for the record the run started from.</summary>
    public required string? StepKey { get; init; }
}

/// <summary>The step to do now, and the ways to do it, most recommended first.</summary>
public sealed class WorkflowNextActionServiceModel
{
    public required string StepKey { get; init; }

    public required string Title { get; init; }

    public required string Help { get; init; }

    public required WorkflowStepRequirement Requirement { get; init; }

    /// <summary>The primary action first, then its alternatives.</summary>
    public required IReadOnlyList<WorkflowActionServiceModel> Actions { get; init; }

    /// <summary>What the step is still waiting for, in plain words. Empty when it can be done now.</summary>
    public required IReadOnlyList<string> WaitingFor { get; init; }
}

public sealed class WorkflowActionServiceModel
{
    public required string CapabilityKey { get; init; }

    /// <summary>The action as a button says it.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Where it opens, relative to the workspace (<c>recipes/3f2…?area=tests</c>), or null when the run does not
    /// yet hold a record the address needs.
    /// </summary>
    public required string? Route { get; init; }

    /// <summary>False for something not built yet.</summary>
    public required bool Available { get; init; }

    /// <summary>The least role that can take it — advice; the capability's own routes decide.</summary>
    public required WorkspaceRole MinimumRole { get; init; }

    /// <summary>Whether the caller can take it now: available, and their role reaches it.</summary>
    public required bool YouCanDoIt { get; init; }
}
