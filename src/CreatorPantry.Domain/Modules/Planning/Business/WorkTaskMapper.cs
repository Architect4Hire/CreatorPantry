using CreatorPantry.Domain.Modules.Planning.Data;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;

namespace CreatorPantry.Domain.Modules.Planning.Business;

/// <summary>A stored task as every planning answer shows it — the capture, the day and the week alike.</summary>
internal static class WorkTaskMapper
{
    /// <summary>For an answer about a task that cannot have labels yet: one just captured.</summary>
    public static readonly ILookup<Guid, WorkTaskLabelRow> NoLabels = LabelsByTask([]);

    public static ILookup<Guid, WorkTaskLabelRow> LabelsByTask(IEnumerable<WorkTaskLabelRow> rows) =>
        rows.ToLookup(row => row.WorkTaskId);

    /// <param name="task">The stored task.</param>
    /// <param name="names">Display names by membership id, read once for every person a whole answer names.</param>
    /// <param name="labels">Labels by task, read once for every task a whole answer shows, already in name order.</param>
    public static WorkTaskServiceModel ToServiceModel(
        WorkTask task, IReadOnlyDictionary<Guid, string> names, ILookup<Guid, WorkTaskLabelRow> labels) =>
        new()
        {
            Id = task.Id,
            Title = task.Title,
            Description = task.Description,
            Status = task.Status,
            Priority = task.Priority,
            ScheduledDate = task.ScheduledDate,
            PlanningWeekStart = task.PlanningWeekStart,
            DueDate = task.DueDate,
            EstimateMinutes = task.EstimateMinutes,
            Owner = Member(task.OwnerMembershipId, names),
            Assignee = Member(task.AssigneeMembershipId, names),
            Links = new WorkTaskLinksServiceModel
            {
                WorkflowRunId = task.WorkflowRunId,
                WorkflowStepKey = task.WorkflowStepKey,
                WorkflowRunRoute = task.WorkflowRunId is { } runId ? WorkTaskPolicy.WorkflowRunRoute(runId) : null,
                RecipeId = task.RecipeId,
                ContentCardId = task.ContentCardId,
                MediaAssetId = task.MediaAssetId,
            },
            Labels = [.. labels[task.Id].Select(row => new WorkTaskLabelServiceModel { Id = row.WorkspaceTagId, Name = row.Name })],
            CreatedAt = task.CreatedAt,
            UpdatedAt = task.UpdatedAt,
            CompletedAt = task.CompletedAt,
            ConcurrencyToken = WorkTaskConcurrencyToken.From(task.RowVersion),
        };

    /// <summary>Null for somebody who has since left the workspace: a task outlives the membership.</summary>
    private static WorkTaskMemberServiceModel Member(Guid membershipId, IReadOnlyDictionary<Guid, string> names) =>
        new() { MembershipId = membershipId, DisplayName = names.GetValueOrDefault(membershipId) };
}
