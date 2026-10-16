using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Workflows.Data;

/// <summary>
/// The bounded reads the dashboard's workflow summary makes (13A.33a). Each is a count the database groups or a
/// list capped at <see cref="WorkflowDashboardPolicy.ListLimit"/>, so the cost does not grow with the workspace,
/// and every one runs under the workspace query filter.
/// </summary>
public interface IWorkflowDashboardRepository
{
    /// <summary>Runs in progress the reader started or was handed. Served by the run list's own index.</summary>
    Task<int> CountMineInProgressAsync(Guid membershipId, CancellationToken cancellationToken);

    /// <summary>Those runs, most recently touched first: the ones to carry on with.</summary>
    Task<IReadOnlyList<WorkflowRunSummaryRecord>> ListMineInProgressAsync(
        Guid membershipId, int limit, CancellationToken cancellationToken);

    /// <summary>Runs in progress with a reason to look at them, for a reader of this role.</summary>
    Task<int> CountNeedingAttentionAsync(WorkflowRunAttentionCriteria attention, CancellationToken cancellationToken);

    /// <summary>Those runs, longest-waiting first — the same order the attention list uses (13A.11).</summary>
    Task<IReadOnlyList<WorkflowRunSummaryRecord>> ListNeedingAttentionAsync(
        WorkflowRunAttentionCriteria attention, int limit, CancellationToken cancellationToken);
}

/// <remarks>
/// Relies on the workspace query filter, as every run read does: nothing here names a workspace, and another
/// workspace's run is in no count and on no list.
/// </remarks>
internal sealed class WorkflowDashboardRepository(CreatorPantryDbContext context) : IWorkflowDashboardRepository
{
    public Task<int> CountMineInProgressAsync(Guid membershipId, CancellationToken cancellationToken) =>
        Mine(membershipId).CountAsync(cancellationToken);

    public async Task<IReadOnlyList<WorkflowRunSummaryRecord>> ListMineInProgressAsync(
        Guid membershipId, int limit, CancellationToken cancellationToken) =>
        await Summaries(Mine(membershipId)
                .OrderByDescending(run => run.UpdatedAt)
                .ThenByDescending(run => run.Id))
            .Take(limit)
            .ToListAsync(cancellationToken);

    public Task<int> CountNeedingAttentionAsync(
        WorkflowRunAttentionCriteria attention, CancellationToken cancellationToken) =>
        WorkflowRunAttentionQuery.NeedingAttention(context.WorkflowRuns.AsNoTracking(), attention)
            .CountAsync(cancellationToken);

    public async Task<IReadOnlyList<WorkflowRunSummaryRecord>> ListNeedingAttentionAsync(
        WorkflowRunAttentionCriteria attention, int limit, CancellationToken cancellationToken) =>
        await Summaries(WorkflowRunAttentionQuery.NeedingAttention(context.WorkflowRuns.AsNoTracking(), attention)
                .OrderBy(run => run.UpdatedAt)
                .ThenBy(run => run.Id))
            .Take(limit)
            .ToListAsync(cancellationToken);

    /// <summary>In progress, and the reader's: started by them or handed to them, as "mine" means in the list.</summary>
    private IQueryable<WorkflowRun> Mine(Guid membershipId) =>
        context.WorkflowRuns.AsNoTracking()
            .Where(run => run.Status == WorkflowRunStatus.InProgress
                && (run.CreatedByMembershipId == membershipId || run.AssignedMembershipId == membershipId));

    /// <summary>The run list's own row shape, so one mapper serves both (<see cref="WorkflowRunSummaryRecord"/>).</summary>
    private static IQueryable<WorkflowRunSummaryRecord> Summaries(IOrderedQueryable<WorkflowRun> runs) =>
        runs.Select(run => new WorkflowRunSummaryRecord(
            run.Id,
            run.Title,
            run.Status,
            run.DefinitionKey,
            run.DefinitionVersion,
            run.CurrentStepKey,
            run.StepCount,
            run.CompletedStepCount,
            run.SkippedStepCount,
            run.CreatedByMembershipId,
            run.AssignedMembershipId,
            run.StartedAt,
            run.UpdatedAt,
            run.CompletedAt,
            run.AbandonedAt));
}

/// <summary>The dashboard's workflow reads, composed (13A.33a). Read-only: it owns no transaction and writes nothing.</summary>
public interface IWorkflowDashboardDataLayer
{
    /// <summary>
    /// Both counts and both lists: four statements, whatever the workspace holds, and nothing read per run.
    /// </summary>
    Task<WorkflowDashboardRead> ReadAsync(
        Guid membershipId, WorkflowRunAttentionCriteria attention, int limit, CancellationToken cancellationToken);
}

/// <summary>The rows and counts of the dashboard's workflow summary, as read.</summary>
public sealed record WorkflowDashboardRead(
    int InProgressCount,
    IReadOnlyList<WorkflowRunSummaryRecord> Mine,
    int NeedsAttentionCount,
    IReadOnlyList<WorkflowRunSummaryRecord> NeedsAttention);

internal sealed class WorkflowDashboardDataLayer(IWorkflowDashboardRepository runs) : IWorkflowDashboardDataLayer
{
    public async Task<WorkflowDashboardRead> ReadAsync(
        Guid membershipId, WorkflowRunAttentionCriteria attention, int limit, CancellationToken cancellationToken)
    {
        var inProgress = await runs.CountMineInProgressAsync(membershipId, cancellationToken);

        // Nobody's runs, nobody's rows: the two lists are skipped rather than asked for, and the counts still stand.
        IReadOnlyList<WorkflowRunSummaryRecord> mine = inProgress == 0
            ? []
            : await runs.ListMineInProgressAsync(membershipId, limit, cancellationToken);

        var attentionCount = await runs.CountNeedingAttentionAsync(attention, cancellationToken);
        IReadOnlyList<WorkflowRunSummaryRecord> needsAttention = attentionCount == 0
            ? []
            : await runs.ListNeedingAttentionAsync(attention, limit, cancellationToken);

        return new WorkflowDashboardRead(inProgress, mine, attentionCount, needsAttention);
    }
}
