namespace CreatorPantry.Domain.Modules.Planning.Managers;

/// <summary>One person as a task names them.</summary>
/// <remarks>
/// The shape a workflow run uses for the same thing, so a client reads people one way across the product.
/// <see cref="DisplayName"/> is null for someone who has since left the workspace: a task outlives the
/// membership that captured it.
/// </remarks>
public sealed record WorkTaskMemberServiceModel
{
    public required Guid MembershipId { get; init; }

    public string? DisplayName { get; init; }
}

/// <summary>One of the workspace's own tags, on a task (13A.27).</summary>
public sealed record WorkTaskLabelServiceModel
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }
}

/// <summary>
/// What a task is about, by id. Pointers, never copies: the linked record's own module says what it is
/// called and whether it still exists, and a task holds no title of its own for any of them.
/// </summary>
public sealed record WorkTaskLinksServiceModel
{
    public Guid? WorkflowRunId { get; init; }

    /// <summary>A step of <see cref="WorkflowRunId"/>; null when the task is about the run as a whole.</summary>
    public string? WorkflowStepKey { get; init; }

    /// <summary>
    /// Where the run is, relative to the workspace — <c>workflows/runs/{id}</c> — so a task can take its person
    /// straight back to the journey it came from (13A.29). Null when the task is about no run.
    /// </summary>
    public string? WorkflowRunRoute { get; init; }

    public Guid? RecipeId { get; init; }

    public Guid? ContentCardId { get; init; }

    public Guid? MediaAssetId { get; init; }
}

/// <summary>One task as the API publishes it (13A.24).</summary>
/// <remarks>
/// <para>
/// <strong>The task's place in its group is not published.</strong> A rank is an ordering device, not a fact
/// about the work; the order a list arrives in is the order.
/// </para>
/// <para>
/// <see cref="DueDate"/>, <see cref="EstimateMinutes"/> and <see cref="CompletedAt"/> are always null on a
/// task just captured. They are here because they are the task's own facts and a client renders one shape
/// whether it has just made the task or read it back.
/// </para>
/// </remarks>
public sealed record WorkTaskServiceModel
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public required WorkTaskStatus Status { get; init; }

    public required WorkTaskPriority Priority { get; init; }

    /// <summary>The day it is planned for, in the workspace's own zone; null when it is planned for no day.</summary>
    public DateOnly? ScheduledDate { get; init; }

    /// <summary>The first day of the week it is planned for; null when it is in no week.</summary>
    public DateOnly? PlanningWeekStart { get; init; }

    public DateOnly? DueDate { get; init; }

    public int? EstimateMinutes { get; init; }

    /// <summary>Who captured it.</summary>
    public required WorkTaskMemberServiceModel Owner { get; init; }

    /// <summary>Who is doing it. The owner until someone hands it on.</summary>
    public required WorkTaskMemberServiceModel Assignee { get; init; }

    public required WorkTaskLinksServiceModel Links { get; init; }

    /// <summary>
    /// The workspace's tags this task carries, by name. A tag retired since it was added still shows, by the
    /// name it had: retiring a tag takes it out of the pickers, not off the work that already carries it.
    /// </summary>
    public required IReadOnlyList<WorkTaskLabelServiceModel> Labels { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>What a later edit quotes back to prove it was composed against this version of the task.</summary>
    public required string ConcurrencyToken { get; init; }
}
