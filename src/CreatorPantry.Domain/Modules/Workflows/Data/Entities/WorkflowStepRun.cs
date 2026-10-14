using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Workflows.Managers;

namespace CreatorPantry.Domain.Modules.Workflows.Data.Entities;

/// <summary>One step of a <see cref="WorkflowRun"/>. Every step of the pinned definition has one from the start.</summary>
/// <remarks>
/// Interior to its run for deletion: a composite foreign key into the run, which cascades, and none to
/// <c>Workspaces</c>, so SQL Server sees one delete path. It carries no concurrency token of its own; see
/// <see cref="WorkflowRun"/>.
/// </remarks>
public class WorkflowStepRun : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid WorkflowRunId { get; set; }

    /// <summary>The step's key in the run's pinned definition. Unique within the run.</summary>
    public string StepKey { get; set; } = string.Empty;

    public WorkflowStepRunStatus Status { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public Guid? CompletedByMembershipId { get; set; }

    public DateTimeOffset? SkippedAt { get; set; }

    public Guid? SkippedByMembershipId { get; set; }

    /// <summary>Why the creator passed it over, in their words, when they said.</summary>
    public string? SkipReason { get; set; }

    /// <summary>
    /// The domain draft this step's work is kept in — a pointer, never the draft. What a step's draft is, and
    /// how it is saved, is 13A.7's; this records only where it is.
    /// </summary>
    public WorkflowResourceKind? DraftResourceKind { get; set; }

    public Guid? DraftResourceId { get; set; }

    public DateTimeOffset? DraftSavedAt { get; set; }
}
