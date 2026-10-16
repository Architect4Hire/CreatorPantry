using CreatorPantry.Domain.Modules.Planning.Data;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;

namespace CreatorPantry.Tests.Planning;

/// <summary>
/// The real work-task repository behind every member, for a test that stages a lost race by overriding the one
/// read the race happens around — the way a concurrent write is put in a window too small to hit on demand.
/// </summary>
internal abstract class DelegatingWorkTaskRepository(IWorkTaskRepository inner) : IWorkTaskRepository
{
    public virtual Task<long?> HighestRankAsync(Guid assigneeMembershipId, WorkTaskStatus status, DateOnly? scheduledDate, CancellationToken cancellationToken) =>
        inner.HighestRankAsync(assigneeMembershipId, status, scheduledDate, cancellationToken);

    public virtual Task<WorkTask?> FindAsync(Guid taskId, CancellationToken cancellationToken) => inner.FindAsync(taskId, cancellationToken);

    public virtual Task<IReadOnlyList<WorkTask>> CompletedBetweenAsync(Guid assigneeMembershipId, DateTimeOffset fromUtc, DateTimeOffset toUtc, int take, CancellationToken cancellationToken) =>
        inner.CompletedBetweenAsync(assigneeMembershipId, fromUtc, toUtc, take, cancellationToken);

    public virtual Task<IReadOnlyList<WorkTask>> OverdueAsync(Guid assigneeMembershipId, DateOnly today, int take, CancellationToken cancellationToken) =>
        inner.OverdueAsync(assigneeMembershipId, today, take, cancellationToken);

    public virtual Task<IReadOnlyList<WorkTask>> ByStatusNotOverdueAsync(Guid assigneeMembershipId, WorkTaskStatus status, DateOnly today, int take, CancellationToken cancellationToken) =>
        inner.ByStatusNotOverdueAsync(assigneeMembershipId, status, today, take, cancellationToken);

    public virtual Task<IReadOnlyList<WorkTask>> ScheduledOnAsync(Guid assigneeMembershipId, DateOnly day, int take, CancellationToken cancellationToken) =>
        inner.ScheduledOnAsync(assigneeMembershipId, day, take, cancellationToken);

    public virtual Task<IReadOnlyList<WorkTask>> UnplannedBacklogAsync(Guid assigneeMembershipId, DateOnly today, int take, CancellationToken cancellationToken) =>
        inner.UnplannedBacklogAsync(assigneeMembershipId, today, take, cancellationToken);

    public virtual void Add(WorkTask task) => inner.Add(task);

    public virtual Task<WorkTask?> FindForUpdateAsync(Guid taskId, CancellationToken cancellationToken) => inner.FindForUpdateAsync(taskId, cancellationToken);

    public virtual Task<List<WorkTaskLabel>> LabelRowsAsync(Guid taskId, CancellationToken cancellationToken) => inner.LabelRowsAsync(taskId, cancellationToken);

    public virtual void AddLabel(WorkTaskLabel label) => inner.AddLabel(label);

    public virtual void RemoveLabels(IEnumerable<WorkTaskLabel> labels) => inner.RemoveLabels(labels);

    public virtual Task<IReadOnlyList<WorkTaskLabelRow>> LabelsForAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken) =>
        inner.LabelsForAsync(taskIds, cancellationToken);

    public virtual Task<IReadOnlySet<Guid>> ActiveTagIdsAsync(IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken) =>
        inner.ActiveTagIdsAsync(tagIds, cancellationToken);

    public virtual Task<WorkTask?> FindInGroupAsync(Guid taskId, Guid assigneeMembershipId, WorkTaskStatus status, DateOnly? scheduledDate, CancellationToken cancellationToken) =>
        inner.FindInGroupAsync(taskId, assigneeMembershipId, status, scheduledDate, cancellationToken);

    public virtual Task<long?> HighestRankBelowAsync(Guid assigneeMembershipId, WorkTaskStatus status, DateOnly? scheduledDate, long rank, CancellationToken cancellationToken) =>
        inner.HighestRankBelowAsync(assigneeMembershipId, status, scheduledDate, rank, cancellationToken);

    public virtual Task<bool> AnyOtherAtRankAsync(Guid assigneeMembershipId, WorkTaskStatus status, DateOnly? scheduledDate, long rank, Guid exceptTaskId, CancellationToken cancellationToken) =>
        inner.AnyOtherAtRankAsync(assigneeMembershipId, status, scheduledDate, rank, exceptTaskId, cancellationToken);

    public virtual Task<WorkTask?> FindFromWorkflowStepAsync(Guid ownerMembershipId, Guid workflowRunId, string workflowStepKey, CancellationToken cancellationToken) =>
        inner.FindFromWorkflowStepAsync(ownerMembershipId, workflowRunId, workflowStepKey, cancellationToken);

    public virtual Task<List<WorkTask>> GroupForUpdateAsync(Guid assigneeMembershipId, WorkTaskStatus status, DateOnly? scheduledDate, CancellationToken cancellationToken) =>
        inner.GroupForUpdateAsync(assigneeMembershipId, status, scheduledDate, cancellationToken);
}
