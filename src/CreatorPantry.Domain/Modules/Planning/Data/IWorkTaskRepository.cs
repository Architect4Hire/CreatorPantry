using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Planning.Data;

/// <summary>EF Core access to one person's work tasks. Every query is already scoped to the resolved workspace.</summary>
public interface IWorkTaskRepository
{
    /// <summary>
    /// The greatest rank in one group — the task at the bottom of it — or null for an empty group.
    /// </summary>
    /// <remarks>
    /// A group is one person's list, as the planner shows it (13A.28): every task in progress, every task
    /// waiting, the not-started tasks of one day, and the not-started tasks of no day. The day divides only the
    /// not-started tasks, because in progress and waiting are one list each whatever day a task was planned
    /// for. That is the set a creator sees in one run and reorders within, so it is the set a rank has to be
    /// unique-ish in. Named as the content board names the same question, so the two read alike.
    /// </remarks>
    Task<long?> HighestRankAsync(
        Guid assigneeMembershipId, WorkTaskStatus status, DateOnly? scheduledDate, CancellationToken cancellationToken);

    /// <summary>
    /// The task with this id in the resolved workspace, or null. Read after a failed save to find out whether
    /// the row landed; see <see cref="IWorkTaskDataLayer.CreateAsync"/>.
    /// </summary>
    Task<WorkTask?> FindAsync(Guid taskId, CancellationToken cancellationToken);

    /// <summary>
    /// One person's tasks finished inside a window of instants, newest first. For the daily view's
    /// "done today" (13A.25), where the window is the local day converted to UTC.
    /// </summary>
    /// <remarks>
    /// A range of instants rather than a comparison of dates, because completion is an instant and the day
    /// it belongs to depends on a zone. <paramref name="toUtc"/> is exclusive, so the midnight that ends one
    /// day is the midnight that begins the next and no task is in both.
    /// </remarks>
    Task<IReadOnlyList<WorkTask>> CompletedBetweenAsync(
        Guid assigneeMembershipId, DateTimeOffset fromUtc, DateTimeOffset toUtc, int take, CancellationToken cancellationToken);

    /// <summary>
    /// One person's unfinished tasks already past their due day, soonest due first.
    /// </summary>
    Task<IReadOnlyList<WorkTask>> OverdueAsync(
        Guid assigneeMembershipId, DateOnly today, int take, CancellationToken cancellationToken);

    /// <summary>
    /// One person's tasks in one status that are not already overdue, most pressing first.
    /// </summary>
    /// <remarks>
    /// The overdue exclusion is in the query rather than applied afterwards: the daily view's groups do not
    /// overlap, and filtering a page after it was cut would silently shorten it.
    /// </remarks>
    Task<IReadOnlyList<WorkTask>> ByStatusNotOverdueAsync(
        Guid assigneeMembershipId, WorkTaskStatus status, DateOnly today, int take, CancellationToken cancellationToken);

    /// <summary>One person's not-started tasks planned for one day and not already overdue.</summary>
    Task<IReadOnlyList<WorkTask>> ScheduledOnAsync(
        Guid assigneeMembershipId, DateOnly day, int take, CancellationToken cancellationToken);

    /// <summary>
    /// One person's not-started tasks with no day at all and not already overdue, most pressing first. The
    /// pool the daily view's suggestions come from (13A.25).
    /// </summary>
    Task<IReadOnlyList<WorkTask>> UnplannedBacklogAsync(
        Guid assigneeMembershipId, DateOnly today, int take, CancellationToken cancellationToken);

    /// <summary>Stages a new task. <c>WorkspaceId</c> is stamped by the ownership interceptor, never here.</summary>
    void Add(WorkTask task);

    /// <summary>
    /// The task with this id in the resolved workspace, tracked, so an edit to it is saved against the row
    /// version it was read at (13A.27). Null for no such task and for another workspace's, alike.
    /// </summary>
    Task<WorkTask?> FindForUpdateAsync(Guid taskId, CancellationToken cancellationToken);

    /// <summary>The label rows one task carries, tracked, so an edit can remove those it no longer names.</summary>
    Task<List<WorkTaskLabel>> LabelRowsAsync(Guid taskId, CancellationToken cancellationToken);

    void AddLabel(WorkTaskLabel label);

    void RemoveLabels(IEnumerable<WorkTaskLabel> labels);

    /// <summary>
    /// The labels of each given task, with each tag's name, retired or not — one statement for a whole answer.
    /// </summary>
    /// <remarks>
    /// The name is read through the label's own foreign key to <c>WorkspaceTag</c>, the entity type that crosses
    /// the module boundary because that key does, as the media library reads its tags.
    /// </remarks>
    Task<IReadOnlyList<WorkTaskLabelRow>> LabelsForAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken);

    /// <summary>Which of the given tags are active tags of the resolved workspace.</summary>
    /// <remarks>
    /// Only a tag a task is newly given is asked about: one it already carries stays after it is retired, so an
    /// unrelated edit never fails because of it.
    /// </remarks>
    Task<IReadOnlySet<Guid>> ActiveTagIdsAsync(IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken);

    /// <summary>
    /// The task with this id if it is in the group, or null — whether it does not exist, is in another group,
    /// or is another workspace's (13A.28). A neighbour a move is placed against.
    /// </summary>
    Task<WorkTask?> FindInGroupAsync(
        Guid taskId, Guid assigneeMembershipId, WorkTaskStatus status, DateOnly? scheduledDate, CancellationToken cancellationToken);

    /// <summary>The greatest rank in the group below <paramref name="rank"/>, or null when nothing ranks lower.</summary>
    Task<long?> HighestRankBelowAsync(
        Guid assigneeMembershipId, WorkTaskStatus status, DateOnly? scheduledDate, long rank, CancellationToken cancellationToken);

    /// <summary>Whether a task other than <paramref name="exceptTaskId"/> in the group shares <paramref name="rank"/>.</summary>
    Task<bool> AnyOtherAtRankAsync(
        Guid assigneeMembershipId,
        WorkTaskStatus status,
        DateOnly? scheduledDate,
        long rank,
        Guid exceptTaskId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The task <paramref name="ownerMembershipId"/> added to their plan from one workflow step, or null (13A.29).
    /// Only a task of <see cref="WorkTaskOrigin.WorkflowStep"/> counts: one pointed at the step by hand is not it.
    /// </summary>
    Task<WorkTask?> FindFromWorkflowStepAsync(
        Guid ownerMembershipId, Guid workflowRunId, string workflowStepKey, CancellationToken cancellationToken);

    /// <summary>The whole group, tracked and in order, for re-spacing when two neighbours have no room between them.</summary>
    Task<List<WorkTask>> GroupForUpdateAsync(
        Guid assigneeMembershipId, WorkTaskStatus status, DateOnly? scheduledDate, CancellationToken cancellationToken);
}

/// <summary>One label on one task, with the tag's name.</summary>
public sealed record WorkTaskLabelRow(Guid WorkTaskId, Guid WorkspaceTagId, string Name);

internal sealed class WorkTaskRepository(CreatorPantryDbContext context) : IWorkTaskRepository
{
    public Task<long?> HighestRankAsync(
        Guid assigneeMembershipId, WorkTaskStatus status, DateOnly? scheduledDate, CancellationToken cancellationToken) =>
        Group(assigneeMembershipId, status, scheduledDate).AsNoTracking()
            .MaxAsync(task => (long?)task.SortRank, cancellationToken);

    public Task<WorkTask?> FindInGroupAsync(
        Guid taskId, Guid assigneeMembershipId, WorkTaskStatus status, DateOnly? scheduledDate, CancellationToken cancellationToken) =>
        Group(assigneeMembershipId, status, scheduledDate).AsNoTracking()
            .FirstOrDefaultAsync(task => task.Id == taskId, cancellationToken);

    public Task<long?> HighestRankBelowAsync(
        Guid assigneeMembershipId, WorkTaskStatus status, DateOnly? scheduledDate, long rank, CancellationToken cancellationToken) =>
        Group(assigneeMembershipId, status, scheduledDate).AsNoTracking()
            .Where(task => task.SortRank < rank)
            .MaxAsync(task => (long?)task.SortRank, cancellationToken);

    public Task<bool> AnyOtherAtRankAsync(
        Guid assigneeMembershipId,
        WorkTaskStatus status,
        DateOnly? scheduledDate,
        long rank,
        Guid exceptTaskId,
        CancellationToken cancellationToken) =>
        Group(assigneeMembershipId, status, scheduledDate).AsNoTracking()
            .AnyAsync(task => task.SortRank == rank && task.Id != exceptTaskId, cancellationToken);

    public Task<WorkTask?> FindFromWorkflowStepAsync(
        Guid ownerMembershipId, Guid workflowRunId, string workflowStepKey, CancellationToken cancellationToken) =>
        context.WorkTasks.AsNoTracking()
            .FirstOrDefaultAsync(
                task => task.Origin == WorkTaskOrigin.WorkflowStep
                    && task.OwnerMembershipId == ownerMembershipId
                    && task.WorkflowRunId == workflowRunId
                    && task.WorkflowStepKey == workflowStepKey,
                cancellationToken);

    public Task<List<WorkTask>> GroupForUpdateAsync(
        Guid assigneeMembershipId, WorkTaskStatus status, DateOnly? scheduledDate, CancellationToken cancellationToken) =>
        Group(assigneeMembershipId, status, scheduledDate)
            .OrderBy(task => task.SortRank)
            .ThenBy(task => task.Id)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// One person's list: a status, and for a task not yet started, a day — null being the tasks of no day. In
    /// progress and waiting are one list each, whatever day their tasks were planned for. See
    /// <see cref="IWorkTaskRepository.HighestRankAsync"/>.
    /// </summary>
    private IQueryable<WorkTask> Group(Guid assigneeMembershipId, WorkTaskStatus status, DateOnly? scheduledDate)
    {
        var mine = context.WorkTasks.Where(task => task.AssigneeMembershipId == assigneeMembershipId && task.Status == status);

        return status == WorkTaskStatus.Backlog
            ? mine.Where(task => task.ScheduledDate == scheduledDate)
            : mine;
    }

    public Task<WorkTask?> FindAsync(Guid taskId, CancellationToken cancellationToken) =>
        context.WorkTasks.AsNoTracking().FirstOrDefaultAsync(task => task.Id == taskId, cancellationToken);

    public Task<IReadOnlyList<WorkTask>> CompletedBetweenAsync(
        Guid assigneeMembershipId, DateTimeOffset fromUtc, DateTimeOffset toUtc, int take, CancellationToken cancellationToken) =>
        ListAsync(
            Mine(assigneeMembershipId)
                .Where(task => task.Status == WorkTaskStatus.Done
                    && task.CompletedAt >= fromUtc
                    && task.CompletedAt < toUtc)
                .OrderByDescending(task => task.CompletedAt)
                .ThenBy(task => task.Id),
            take,
            cancellationToken);

    public Task<IReadOnlyList<WorkTask>> OverdueAsync(
        Guid assigneeMembershipId, DateOnly today, int take, CancellationToken cancellationToken) =>
        ListAsync(
            Mine(assigneeMembershipId)
                .Where(task => task.Status != WorkTaskStatus.Done && task.DueDate != null && task.DueDate < today)
                .OrderBy(task => task.DueDate)
                .ThenByDescending(task => task.Priority)
                .ThenBy(task => task.SortRank)
                .ThenBy(task => task.Id),
            take,
            cancellationToken);

    public Task<IReadOnlyList<WorkTask>> ByStatusNotOverdueAsync(
        Guid assigneeMembershipId, WorkTaskStatus status, DateOnly today, int take, CancellationToken cancellationToken) =>
        ListAsync(
            MostPressing(Mine(assigneeMembershipId).Where(task => task.Status == status), today),
            take,
            cancellationToken);

    public Task<IReadOnlyList<WorkTask>> ScheduledOnAsync(
        Guid assigneeMembershipId, DateOnly day, int take, CancellationToken cancellationToken) =>
        ListAsync(
            MostPressing(
                Mine(assigneeMembershipId)
                    .Where(task => task.Status == WorkTaskStatus.Backlog && task.ScheduledDate == day),
                day),
            take,
            cancellationToken);

    public Task<IReadOnlyList<WorkTask>> UnplannedBacklogAsync(
        Guid assigneeMembershipId, DateOnly today, int take, CancellationToken cancellationToken) =>
        ListAsync(
            MostPressing(
                Mine(assigneeMembershipId)
                    .Where(task => task.Status == WorkTaskStatus.Backlog && task.ScheduledDate == null),
                today),
            take,
            cancellationToken);

    public void Add(WorkTask task) => context.WorkTasks.Add(task);

    public Task<WorkTask?> FindForUpdateAsync(Guid taskId, CancellationToken cancellationToken) =>
        context.WorkTasks.FirstOrDefaultAsync(task => task.Id == taskId, cancellationToken);

    public Task<List<WorkTaskLabel>> LabelRowsAsync(Guid taskId, CancellationToken cancellationToken) =>
        context.WorkTaskLabels.Where(label => label.WorkTaskId == taskId).ToListAsync(cancellationToken);

    public void AddLabel(WorkTaskLabel label) => context.WorkTaskLabels.Add(label);

    public void RemoveLabels(IEnumerable<WorkTaskLabel> labels) => context.WorkTaskLabels.RemoveRange(labels);

    public async Task<IReadOnlyList<WorkTaskLabelRow>> LabelsForAsync(
        IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken)
    {
        if (taskIds.Count == 0)
        {
            return [];
        }

        // Both sides are workspace-owned and filtered, and the key between them is workspace-paired, so a
        // label can only ever name a tag of its own workspace.
        return await context.WorkTaskLabels.AsNoTracking()
            .Where(label => taskIds.Contains(label.WorkTaskId))
            .Join(
                context.WorkspaceTags.AsNoTracking(),
                label => label.WorkspaceTagId,
                tag => tag.Id,
                (label, tag) => new { label.WorkTaskId, TagId = tag.Id, tag.Name })
            .OrderBy(row => row.Name)
            .ThenBy(row => row.TagId)
            .Select(row => new WorkTaskLabelRow(row.WorkTaskId, row.TagId, row.Name))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<Guid>> ActiveTagIdsAsync(
        IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken)
    {
        if (tagIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var active = await context.WorkspaceTags.AsNoTracking()
            .Where(tag => tagIds.Contains(tag.Id) && tag.IsActive)
            .Select(tag => tag.Id)
            .ToListAsync(cancellationToken);

        return active.ToHashSet();
    }

    /// <summary>
    /// One person's tasks in the resolved workspace. The workspace itself is the global filter's, so this
    /// names only the person — and naming them is what keeps one member's list out of another's.
    /// </summary>
    private IQueryable<WorkTask> Mine(Guid assigneeMembershipId) =>
        context.WorkTasks.AsNoTracking().Where(task => task.AssigneeMembershipId == assigneeMembershipId);

    /// <summary>Drops what is already counted as overdue, then orders by how much it presses.</summary>
    private static IQueryable<WorkTask> MostPressing(IQueryable<WorkTask> tasks, DateOnly today) =>
        tasks
            .Where(task => task.DueDate == null || task.DueDate >= today)
            .OrderByDescending(task => task.Priority)
            .ThenBy(task => task.SortRank)
            .ThenBy(task => task.Id);

    private static async Task<IReadOnlyList<WorkTask>> ListAsync(
        IQueryable<WorkTask> query, int take, CancellationToken cancellationToken) =>
        await query.Take(take).ToListAsync(cancellationToken);
}
