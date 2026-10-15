using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Workflows.Data;

/// <summary>The run list's reads (13A.11): run rows and their links, never a step row.</summary>
public interface IWorkflowRunListRepository
{
    /// <summary>One page of runs in the list's order, read one past the limit so the caller can tell whether another follows.</summary>
    Task<(IReadOnlyList<WorkflowRunSummaryRecord> Rows, bool HasMore)> ListAsync(
        WorkflowRunListCriteria criteria, CancellationToken cancellationToken);

    /// <summary>Every link of these runs, by kind and id — one statement however many runs there are.</summary>
    Task<IReadOnlyList<WorkflowRunLinkReferenceRecord>> ListLinksAsync(
        IReadOnlyCollection<Guid> runIds, CancellationToken cancellationToken);
}

/// <summary>A run's own columns that a list shows. No step, draft, reason or route.</summary>
public sealed record WorkflowRunSummaryRecord(
    Guid Id,
    string Title,
    WorkflowRunStatus Status,
    string DefinitionKey,
    int DefinitionVersion,
    string? CurrentStepKey,
    int StepCount,
    int CompletedStepCount,
    int SkippedStepCount,
    Guid CreatedByMembershipId,
    Guid? AssignedMembershipId,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? AbandonedAt);

public sealed record WorkflowRunLinkReferenceRecord(
    Guid Id,
    Guid WorkflowRunId,
    WorkflowResourceKind ResourceKind,
    Guid ResourceId,
    string? StepKey,
    DateTimeOffset LinkedAt);

/// <remarks>
/// Relies on the workspace query filter, as every run read does: nothing here names a workspace, and another
/// workspace's run is not in any page.
/// </remarks>
internal sealed class WorkflowRunListRepository(CreatorPantryDbContext context) : IWorkflowRunListRepository
{
    public async Task<(IReadOnlyList<WorkflowRunSummaryRecord> Rows, bool HasMore)> ListAsync(
        WorkflowRunListCriteria criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var runs = context.WorkflowRuns.AsNoTracking();

        if (criteria.DefinitionKey is { } definition)
        {
            runs = runs.Where(run => run.DefinitionKey == definition);
        }

        var me = criteria.MembershipId;
        runs = criteria.View switch
        {
            WorkflowRunListView.Mine => runs.Where(run => run.Status == WorkflowRunStatus.InProgress
                && (run.CreatedByMembershipId == me || run.AssignedMembershipId == me)),
            WorkflowRunListView.Completed => runs.Where(run => run.Status == WorkflowRunStatus.Completed),
            WorkflowRunListView.Attention => NeedingAttention(runs, criteria.Attention),
            _ => runs,
        };

        if (criteria.Position is { } position)
        {
            runs = criteria.Ascending
                ? runs.Where(run => run.UpdatedAt > position.UpdatedAt
                    || (run.UpdatedAt == position.UpdatedAt && run.Id.CompareTo(position.RunId) > 0))
                : runs.Where(run => run.UpdatedAt < position.UpdatedAt
                    || (run.UpdatedAt == position.UpdatedAt && run.Id.CompareTo(position.RunId) < 0));
        }

        var ordered = criteria.Ascending
            ? runs.OrderBy(run => run.UpdatedAt).ThenBy(run => run.Id)
            : runs.OrderByDescending(run => run.UpdatedAt).ThenByDescending(run => run.Id);

        var fetched = await ordered

            // One row past the limit: how the page learns whether another follows without a second query.
            .Take(criteria.Limit + 1)
            .Select(run => new WorkflowRunSummaryRecord(
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
                run.AbandonedAt))
            .ToListAsync(cancellationToken);

        return ([.. fetched.Take(criteria.Limit)], fetched.Count > criteria.Limit);
    }

    public async Task<IReadOnlyList<WorkflowRunLinkReferenceRecord>> ListLinksAsync(
        IReadOnlyCollection<Guid> runIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runIds);

        if (runIds.Count == 0)
        {
            return [];
        }

        return await context.WorkflowRunLinks.AsNoTracking()
            .Where(link => runIds.Contains(link.WorkflowRunId))
            .Select(link => new WorkflowRunLinkReferenceRecord(
                link.Id, link.WorkflowRunId, link.ResourceKind, link.ResourceId, link.StepKey, link.LinkedAt))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// In progress, and idle, on an outdated version, or on a step the reader cannot take — each matched on the run's
    /// own columns, the version and step built into the same strings <see cref="WorkflowRunAttentionCriteria"/> lists.
    /// </summary>
    private static IQueryable<WorkflowRun> NeedingAttention(
        IQueryable<WorkflowRun> runs, WorkflowRunAttentionCriteria attention)
    {
        var idleBefore = attention.IdleBefore;
        var outdated = attention.OutdatedVersions;
        var blocked = attention.BlockedSteps;

        return runs.Where(run => run.Status == WorkflowRunStatus.InProgress
            && (run.UpdatedAt < idleBefore
                || outdated.Contains(run.DefinitionKey + WorkflowRunAttentionCriteria.VersionSeparator + run.DefinitionVersion.ToString())
                || blocked.Contains(run.DefinitionKey + WorkflowRunAttentionCriteria.VersionSeparator + run.DefinitionVersion.ToString()
                    + WorkflowRunAttentionCriteria.StepSeparator + run.CurrentStepKey)));
    }
}
