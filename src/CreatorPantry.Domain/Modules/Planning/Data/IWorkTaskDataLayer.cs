using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Planning.Data;

/// <summary>What became of an edit's save.</summary>
public enum WorkTaskWrite
{
    Saved = 0,

    /// <summary>The task changed or went since it was read. Nothing was written.</summary>
    Stale = 1,

    /// <summary>The store refused the write — a tag removed in between, or a transient fault. Nothing was written.</summary>
    Refused = 2,

    /// <summary>The task a move was to be placed before is not in the list it was moving to. Nothing was written.</summary>
    PlaceUnusable = 3,

    /// <summary>The task was already exactly where a reorder asked for it. Nothing was written.</summary>
    Unchanged = 4,
}

/// <summary>Complete persistence operations for work tasks, and the transaction boundary around them.</summary>
public interface IWorkTaskDataLayer
{
    /// <inheritdoc cref="IWorkTaskRepository.FindForUpdateAsync"/>
    Task<WorkTask?> FindForUpdateAsync(Guid taskId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IWorkTaskRepository.LabelsForAsync"/>
    Task<IReadOnlyList<WorkTaskLabelRow>> LabelsForAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken);

    /// <inheritdoc cref="IWorkTaskRepository.ActiveTagIdsAsync"/>
    Task<IReadOnlySet<Guid>> ActiveTagIdsAsync(IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken);

    /// <summary>
    /// Saves an edit to a tracked task (13A.27): its fields as Business left them, its labels when
    /// <paramref name="labelIds"/> is given, and — when <paramref name="regroup"/> — a new place at the bottom of
    /// the list it now belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One save, against the row version the task was read at.</strong> Business always writes the
    /// task's own row on an edit, so a change of labels alone still moves the token and is still refused when
    /// somebody else's edit landed first.
    /// </para>
    /// <para>
    /// A rank means something only within one person's list for one day, so a task handed to someone else or
    /// planned for another day takes a place at the bottom of its new list, read here immediately before the
    /// save — exactly as a capture does.
    /// </para>
    /// </remarks>
    Task<WorkTaskWrite> UpdateAsync(
        WorkTask task, IReadOnlySet<Guid>? labelIds, bool regroup, CancellationToken cancellationToken);

    /// <summary>
    /// Saves a move (13A.28): a tracked task whose status, day and week Business has already set, given a place
    /// in the list it now belongs to — immediately before <paramref name="beforeTaskId"/>, or at the bottom.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One write.</strong> The task normally takes the midpoint between its new neighbours, so one row
    /// changes. When they have no room between them — adjacent ranks, or a tie at the neighbour's rank — the
    /// whole target list is re-spaced in order, with the task in its place, in the same save. No other list is
    /// touched.
    /// </para>
    /// <para>
    /// Every row in the save is checked against the row version it was read at, so a move that raced another
    /// write to the task — or to a neighbour it had to re-space — is <see cref="WorkTaskWrite.Stale"/> and
    /// writes nothing, the idempotency record the caller staged included.
    /// </para>
    /// </remarks>
    Task<WorkTaskWrite> MoveAsync(WorkTask task, Guid? beforeTaskId, CancellationToken cancellationToken);

    /// <summary>
    /// Gives a tracked task a new place in the list it is already in (13A.28a): immediately before
    /// <paramref name="beforeTaskId"/>, or at the bottom.
    /// </summary>
    /// <remarks>
    /// The move's placement exactly — a midpoint between neighbours, or the list re-spaced in its own order in the
    /// same save when there is no room — with one difference: a task already exactly there is
    /// <see cref="WorkTaskWrite.Unchanged"/> and nothing is written, so its token stays good. That is judged from
    /// ranks alone, never from how an engine orders ids. <paramref name="updatedAt"/> is stamped only on a write.
    /// </remarks>
    Task<WorkTaskWrite> ReorderAsync(
        WorkTask task, Guid? beforeTaskId, DateTimeOffset updatedAt, CancellationToken cancellationToken);

    /// <summary>
    /// Saves a tracked task as Business left it, in one save against the row version it was read at (13A.28b):
    /// completing or reopening, which change the task's own row and nothing else. An idempotency record the
    /// caller staged commits with it or not at all.
    /// </summary>
    Task<WorkTaskWrite> SaveAsync(WorkTask task, CancellationToken cancellationToken);

    /// <inheritdoc cref="IWorkTaskRepository.FindFromWorkflowStepAsync"/>
    Task<WorkTask?> FindFromWorkflowStepAsync(
        Guid ownerMembershipId, Guid workflowRunId, string workflowStepKey, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a task from a workflow step (13A.29), or answers with the one its owner already has for that step.
    /// Null when neither: nothing was written and the request may be sent again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One per person per step, kept by the store.</strong> The existing task is looked for first; a
    /// confirm that races another past that look still cannot make a second, because the filtered unique index
    /// refuses it — and the one that lost reads the winner's task back and answers with it, not with an error.
    /// </para>
    /// <para>
    /// A new task goes to the bottom of its list, as a capture does, and an idempotency record the caller staged
    /// commits with it.
    /// </para>
    /// </remarks>
    Task<(WorkTask Task, bool Created)?> AddFromWorkflowStepAsync(WorkTask task, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a new task at the bottom of its group.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The place is decided here, inside the operation that writes it</strong>: one gap below the
    /// group's lowest task, read immediately before the save. Two captures racing into one group may read the
    /// same lowest task and take the same rank; both are written, and the order — rank, then id — still reads
    /// them one way every time. That is what the gapped rank is for, so there is no retry.
    /// </para>
    /// <para>
    /// The unit of work is the request's own <c>DbContext</c>, so an idempotency record staged by the caller
    /// commits with this row or not at all. False means nothing was written and nothing is left staged, which
    /// is what lets the caller say a refusal is safe to send again.
    /// </para>
    /// <para>
    /// <strong>Whether the row landed is answered by looking, not by reading the exception.</strong> EF wraps
    /// a dropped connection or a timeout in the same <c>DbUpdateException</c> as a broken constraint, and the
    /// first two can be raised over a commit that in fact succeeded. Reporting that as "nothing written"
    /// would invite a retry that captures the task twice, so the row is read back before anything is claimed.
    /// A read that cannot be answered either is left to propagate: the honest answer is then that nobody
    /// knows, and a 500 promises nothing about retrying where a 409 would.
    /// </para>
    /// </remarks>
    Task<bool> CreateAsync(WorkTask task, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a new task and the labels it was captured with, in the one save <see cref="CreateAsync"/> makes, so a
    /// task never lands without the labels it was asked for (13A.31).
    /// </summary>
    Task<bool> CreateAsync(WorkTask task, IReadOnlyCollection<Guid> labelIds, CancellationToken cancellationToken);

    /// <summary>
    /// Every group of one person's day, read in one pass (13A.25).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Six indexed reads, each asking for one more row than it will return so the caller can say the group
    /// was cut without a second counting query. The groups are made not to overlap in the queries
    /// themselves, so a group that is cut is cut from rows that genuinely belong to it.
    /// </para>
    /// <para>
    /// <paramref name="dayStartsAtUtc"/> and <paramref name="dayEndsAtUtc"/> bound the local day as instants,
    /// which is the only form "finished today" can take: a completion is an instant, and the day it falls in
    /// is a question about a zone the caller has already resolved.
    /// </para>
    /// </remarks>
    Task<WorkTaskDay> ReadDayAsync(
        Guid assigneeMembershipId,
        DateOnly today,
        DateTimeOffset dayStartsAtUtc,
        DateTimeOffset dayEndsAtUtc,
        bool withSuggestions,
        CancellationToken cancellationToken);
}

/// <summary>
/// One person's day as the store holds it: each group with one row more than will be shown, so the caller
/// can tell a full group from a cut one.
/// </summary>
public sealed record WorkTaskDay(
    IReadOnlyList<WorkTask> Overdue,
    IReadOnlyList<WorkTask> ScheduledToday,
    IReadOnlyList<WorkTask> InProgress,
    IReadOnlyList<WorkTask> Waiting,
    IReadOnlyList<WorkTask> CompletedToday,
    IReadOnlyList<WorkTask> Unplanned,
    IReadOnlyList<WorkTaskLabelRow> Labels);

internal sealed class WorkTaskDataLayer(IWorkTaskRepository repository, CreatorPantryDbContext db) : IWorkTaskDataLayer
{
    public Task<WorkTask?> FindForUpdateAsync(Guid taskId, CancellationToken cancellationToken) =>
        repository.FindForUpdateAsync(taskId, cancellationToken);

    public Task<IReadOnlyList<WorkTaskLabelRow>> LabelsForAsync(
        IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken) =>
        repository.LabelsForAsync(taskIds, cancellationToken);

    public Task<IReadOnlySet<Guid>> ActiveTagIdsAsync(IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken) =>
        repository.ActiveTagIdsAsync(tagIds, cancellationToken);

    public async Task<WorkTaskWrite> MoveAsync(WorkTask task, Guid? beforeTaskId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);

        var (assignee, status, day) = (task.AssigneeMembershipId, task.Status, task.ScheduledDate);

        // The stored row still holds the list it is leaving, so nothing below counts it in the one it is joining.
        if (beforeTaskId is not { } beforeId)
        {
            var bottom = await repository.HighestRankAsync(assignee, status, day, cancellationToken);
            task.SortRank = (bottom ?? 0) + WorkTaskPolicy.RankGap;

            return await CommitAsync(cancellationToken);
        }

        // A move to another week's backlog stays in the list of no day, so the task may still be in the list it is
        // joining. It is never its own neighbour.
        if (beforeId == task.Id
            || await repository.FindInGroupAsync(beforeId, assignee, status, day, cancellationToken) is not { } before)
        {
            return WorkTaskWrite.PlaceUnusable;
        }

        var below = await repository.HighestRankBelowAsync(assignee, status, day, before.SortRank, cancellationToken);
        await PlaceBeforeAsync(task, before, below, cancellationToken);

        return await CommitAsync(cancellationToken);
    }

    public async Task<WorkTaskWrite> ReorderAsync(
        WorkTask task, Guid? beforeTaskId, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);

        var (assignee, status, day) = (task.AssigneeMembershipId, task.Status, task.ScheduledDate);

        // Unlike a move, the stored row is in this list, so every question below may find the task itself.
        if (beforeTaskId is not { } beforeId)
        {
            var bottom = await repository.HighestRankAsync(assignee, status, day, cancellationToken);

            // Already last, and alone on its rank: nothing to do.
            if (bottom == task.SortRank
                && !await repository.AnyOtherAtRankAsync(assignee, status, day, task.SortRank, task.Id, cancellationToken))
            {
                return WorkTaskWrite.Unchanged;
            }

            task.SortRank = (bottom ?? 0) + WorkTaskPolicy.RankGap;
        }
        else
        {
            if (beforeId == task.Id
                || await repository.FindInGroupAsync(beforeId, assignee, status, day, cancellationToken) is not { } before)
            {
                return WorkTaskWrite.PlaceUnusable;
            }

            var below = await repository.HighestRankBelowAsync(assignee, status, day, before.SortRank, cancellationToken);

            // Already immediately before it: the task is the greatest rank below its neighbour, and neither of
            // the two shares its rank with anything that could come between them.
            if (below == task.SortRank
                && !await repository.AnyOtherAtRankAsync(assignee, status, day, task.SortRank, task.Id, cancellationToken)
                && !await repository.AnyOtherAtRankAsync(assignee, status, day, before.SortRank, before.Id, cancellationToken))
            {
                return WorkTaskWrite.Unchanged;
            }

            await PlaceBeforeAsync(task, before, below, cancellationToken);
        }

        task.UpdatedAt = updatedAt;

        return await CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Places a tracked task immediately before <paramref name="before"/> in its list: one gap above it when nothing
    /// ranks lower, the midpoint when there is room, and otherwise the whole list re-spaced in its own order with
    /// the task in its place. <paramref name="below"/> is the greatest rank in the list below the neighbour's.
    /// </summary>
    private async Task PlaceBeforeAsync(WorkTask task, WorkTask before, long? below, CancellationToken cancellationToken)
    {
        var (assignee, status, day) = (task.AssigneeMembershipId, task.Status, task.ScheduledDate);
        var tied = await repository.AnyOtherAtRankAsync(assignee, status, day, before.SortRank, before.Id, cancellationToken);

        if (!tied && below is null)
        {
            // First in the list: one gap above the task it goes before. Ranks may go below zero.
            task.SortRank = before.SortRank - WorkTaskPolicy.RankGap;
        }
        else if (!tied && before.SortRank - below!.Value > 1)
        {
            task.SortRank = below.Value + ((before.SortRank - below.Value) / 2);
        }
        else
        {
            // No room, or another task shares the neighbour's rank and "immediately before" would be ambiguous:
            // re-space the list in its own order, with the task in its place.
            var list = await repository.GroupForUpdateAsync(assignee, status, day, cancellationToken);

            // Taken out of where it was before it is put where it is going, so it is ranked once — a reorder, and a
            // move to another week's backlog, both start inside this list.
            list.RemoveAll(candidate => candidate.Id == task.Id);
            var place = list.FindIndex(candidate => candidate.Id == before.Id);
            list.Insert(place < 0 ? list.Count : place, task);

            for (var index = 0; index < list.Count; index++)
            {
                list[index].SortRank = (index + 1) * WorkTaskPolicy.RankGap;
            }
        }
    }

    public Task<WorkTask?> FindFromWorkflowStepAsync(
        Guid ownerMembershipId, Guid workflowRunId, string workflowStepKey, CancellationToken cancellationToken) =>
        repository.FindFromWorkflowStepAsync(ownerMembershipId, workflowRunId, workflowStepKey, cancellationToken);

    public async Task<(WorkTask Task, bool Created)?> AddFromWorkflowStepAsync(WorkTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);

        var (owner, runId, stepKey) = (task.OwnerMembershipId, task.WorkflowRunId!.Value, task.WorkflowStepKey!);

        if (await repository.FindFromWorkflowStepAsync(owner, runId, stepKey, cancellationToken) is { } existing)
        {
            return (existing, false);
        }

        if (await CreateAsync(task, cancellationToken))
        {
            return (task, true);
        }

        // Refused. Most likely a confirm that got there first — the index's job — so its task is the answer.
        return await repository.FindFromWorkflowStepAsync(owner, runId, stepKey, cancellationToken) is { } winner
            ? (winner, false)
            : null;
    }

    public Task<WorkTaskWrite> SaveAsync(WorkTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);

        return CommitAsync(cancellationToken);
    }

    /// <summary>One save of everything staged, with what it means when it does not happen.</summary>
    private async Task<WorkTaskWrite> CommitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);

            return WorkTaskWrite.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            // The task, or a neighbour that had to be re-spaced, changed first. Nothing was written.
            db.ChangeTracker.Clear();

            return WorkTaskWrite.Stale;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();

            return WorkTaskWrite.Refused;
        }
    }

    public async Task<WorkTaskWrite> UpdateAsync(
        WorkTask task, IReadOnlySet<Guid>? labelIds, bool regroup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (regroup)
        {
            // The stored row still holds its old list, so it is not counted in the one it is joining.
            var bottom = await repository.HighestRankAsync(
                task.AssigneeMembershipId, task.Status, task.ScheduledDate, cancellationToken);

            task.SortRank = (bottom ?? 0) + WorkTaskPolicy.RankGap;
        }

        if (labelIds is not null)
        {
            var current = await repository.LabelRowsAsync(task.Id, cancellationToken);

            repository.RemoveLabels(current.Where(label => !labelIds.Contains(label.WorkspaceTagId)));

            foreach (var tagId in labelIds.Where(id => current.All(label => label.WorkspaceTagId != id)))
            {
                repository.AddLabel(new WorkTaskLabel { WorkTaskId = task.Id, WorkspaceTagId = tagId });
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);

            return WorkTaskWrite.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Somebody else's edit landed first, or the task is gone. The labels were in the same save.
            db.ChangeTracker.Clear();

            return WorkTaskWrite.Stale;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();

            return WorkTaskWrite.Refused;
        }
    }

    public Task<bool> CreateAsync(WorkTask task, CancellationToken cancellationToken) =>
        CreateAsync(task, [], cancellationToken);

    public async Task<bool> CreateAsync(WorkTask task, IReadOnlyCollection<Guid> labelIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(labelIds);

        foreach (var tagId in labelIds)
        {
            repository.AddLabel(new WorkTaskLabel { WorkTaskId = task.Id, WorkspaceTagId = tagId });
        }

        var bottom = await repository.HighestRankAsync(
            task.AssigneeMembershipId, task.Status, task.ScheduledDate, cancellationToken);

        task.SortRank = (bottom ?? 0) + WorkTaskPolicy.RankGap;

        repository.Add(task);

        try
        {
            await db.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateException)
        {
            // Nothing of this one is left staged, whichever way it failed.
            db.ChangeTracker.Clear();

            if (await repository.FindAsync(task.Id, cancellationToken) is not { } committed)
            {
                // A check constraint or a lost race: the row is not there, so neither is the idempotency
                // record that shared the save, and the caller may offer the request again as it stands.
                return false;
            }

            // It did land. The in-memory copy never received the row version the store generated, and the
            // token the caller is about to be handed has to be the committed one or their next edit is
            // refused against a version that never existed.
            task.RowVersion = committed.RowVersion;

            return true;
        }
    }

    public async Task<WorkTaskDay> ReadDayAsync(
        Guid assigneeMembershipId,
        DateOnly today,
        DateTimeOffset dayStartsAtUtc,
        DateTimeOffset dayEndsAtUtc,
        bool withSuggestions,
        CancellationToken cancellationToken)
    {
        // One past the cap, so a full group and a cut one are distinguishable without counting.
        const int probe = WorkTaskPolicy.TodayGroupMaxRows + 1;

        // Sequential rather than concurrent: one DbContext serves one request and cannot be used by two
        // reads at once. Each is a single indexed seek, so the cost is in the round trips, not the work.
        var overdue = await repository.OverdueAsync(assigneeMembershipId, today, probe, cancellationToken);
        var scheduled = await repository.ScheduledOnAsync(assigneeMembershipId, today, probe, cancellationToken);
        var inProgress = await repository.ByStatusNotOverdueAsync(
            assigneeMembershipId, WorkTaskStatus.InProgress, today, probe, cancellationToken);
        var waiting = await repository.ByStatusNotOverdueAsync(
            assigneeMembershipId, WorkTaskStatus.Waiting, today, probe, cancellationToken);
        var completed = await repository.CompletedBetweenAsync(
            assigneeMembershipId, dayStartsAtUtc, dayEndsAtUtc, probe, cancellationToken);

        // The sixth read happens only when suggestions were asked for: nobody pays for an offer they did
        // not want.
        var unplanned = withSuggestions
            ? await repository.UnplannedBacklogAsync(
                assigneeMembershipId, today, WorkTaskPolicy.TodaySuggestionCount, cancellationToken)
            : [];

        // The labels of every row the day shows, in one statement, whatever the day holds.
        var labels = await repository.LabelsForAsync(
            [.. new[] { overdue, scheduled, inProgress, waiting, completed, unplanned }.SelectMany(group => group).Select(task => task.Id)],
            cancellationToken);

        return new WorkTaskDay(overdue, scheduled, inProgress, waiting, completed, unplanned, labels);
    }
}
