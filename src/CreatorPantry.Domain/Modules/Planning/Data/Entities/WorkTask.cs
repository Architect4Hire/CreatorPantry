using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Planning.Managers;

namespace CreatorPantry.Domain.Modules.Planning.Data.Entities;

/// <summary>
/// One piece of one person's work — "shoot the galette", "answer the brand email" (13A.23). Workspace-owned,
/// and the root of its aggregate: its labels hang off it and go when it goes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The person's work, never the content's state.</strong> A task says what someone means to do and how
/// far they have got. It is not an approval, a board column or a publication, and completing one completes
/// nothing else: a workflow step is done when its own rule is satisfied, a card moves when someone moves it.
/// </para>
/// <para>
/// <strong>Links are pointers, not foreign keys.</strong> <see cref="RecipeId"/>, <see cref="ContentCardId"/>,
/// <see cref="MediaAssetId"/> and <see cref="WorkflowRunId"/> name records other modules own, as
/// <c>WorkflowRunLink</c> does and for its reasons: a to-do must neither cascade into a recipe nor stand in the
/// way of archiving one, and a <c>SET NULL</c> from each would be a second cascade path SQL Server refuses. The
/// write seam checks each id through the owning module's facade in the resolved workspace, and a read shows a
/// link whose record has gone as no longer available rather than failing. The task holds no copy of what it
/// links to — not even a title.
/// </para>
/// <para>
/// <strong>Days are calendar dates, not instants.</strong> <see cref="ScheduledDate"/>, <see cref="PlanningWeekStart"/>
/// and <see cref="DueDate"/> are the days the person means, in their own zone; which day is "today" is the
/// query's to decide (13A.25), so a task never moves because a zone was read differently.
/// </para>
/// <para>
/// <strong>People are memberships, not users</strong>, and none is a foreign key, as no actor id in this schema
/// is. <see cref="AssigneeMembershipId"/> is required and starts as the owner, so "my tasks" is one column.
/// </para>
/// </remarks>
public class WorkTask : IWorkspaceOwned
{
    public Guid Id { get; set; }

    /// <summary>Stamped by <c>WorkspaceOwnershipInterceptor</c> from the resolved context, never by hand.</summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>Who captured it.</summary>
    public Guid OwnerMembershipId { get; set; }

    /// <summary>Who is doing it. The owner unless someone handed it on (13A.27).</summary>
    public Guid AssigneeMembershipId { get; set; }

    /// <summary>The person's own words, stored as typed.</summary>
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public WorkTaskStatus Status { get; set; } = WorkTaskStatus.Backlog;

    public WorkTaskPriority Priority { get; set; } = WorkTaskPriority.Normal;

    /// <summary>The day it is planned for. Requires <see cref="PlanningWeekStart"/>, the week holding that day.</summary>
    public DateOnly? ScheduledDate { get; set; }

    /// <summary>
    /// The first day of the week it is planned for, with or without a day in it. That the day falls inside the
    /// week is the write seam's to check: no date arithmetic is portable across both engines.
    /// </summary>
    public DateOnly? PlanningWeekStart { get; set; }

    public DateOnly? DueDate { get; set; }

    /// <summary>How long the person expects it to take. Null is unknown, and never counted as nothing (13A.26).</summary>
    public int? EstimateMinutes { get; set; }

    /// <summary>The task's place within its group. Lower is higher up; see <see cref="WorkTaskPolicy.RankGap"/>.</summary>
    public long SortRank { get; set; }

    /// <summary>
    /// The recurring template this task was made from (13A.30), with <see cref="RecurrenceDate"/>: whole or
    /// absent. A pointer until 13A.30 adds the template table.
    /// </summary>
    public Guid? WorkTaskTemplateId { get; set; }

    /// <summary>Which occurrence of <see cref="WorkTaskTemplateId"/> this is. One task per occurrence.</summary>
    public DateOnly? RecurrenceDate { get; set; }

    /// <summary>The workflow run this task is about, when it came from one (13A.29).</summary>
    public Guid? WorkflowRunId { get; set; }

    /// <summary>A step of <see cref="WorkflowRunId"/>, when the task is about one step.</summary>
    public string? WorkflowStepKey { get; set; }

    /// <summary>The recipe as it stands, not a version: a task tracks work, it does not pin content.</summary>
    public Guid? RecipeId { get; set; }

    public Guid? ContentCardId { get; set; }

    public Guid? MediaAssetId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Set with <see cref="WorkTaskStatus.Done"/> and only with it.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <inheritdoc cref="CompletedAt"/>
    public Guid? CompletedByMembershipId { get; set; }

    /// <summary>
    /// The status it was completed from, which reopening restores (13A.28b). Its day and week are not cleared by
    /// completing it, so the rest of where it was planned survives too.
    /// </summary>
    public WorkTaskStatus? CompletedFromStatus { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
