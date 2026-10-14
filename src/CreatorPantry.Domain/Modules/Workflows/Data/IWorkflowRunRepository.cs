using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Workflows.Data;

public interface IWorkflowRunRepository
{
    /// <summary>Stages a new run with every one of its step rows and links.</summary>
    void Add(WorkflowRun run, IReadOnlyList<WorkflowStepRun> steps, IReadOnlyList<WorkflowRunLink> links);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>One run with its step rows and links, untracked, or null when this workspace has no such run.</summary>
    Task<WorkflowRunRecord?> FindAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>The same, tracked — the run, its step rows and its links — for a change to be saved together.</summary>
    Task<WorkflowRunRecord?> FindForUpdateAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>
    /// Saves the tracked run against the row version the caller read, so a change made in between is refused rather
    /// than overwritten.
    /// </summary>
    Task SaveChangesAsync(WorkflowRun run, byte[] expectedRowVersion, CancellationToken cancellationToken);

    /// <summary>Forgets everything tracked, after a save that did not happen.</summary>
    void Forget();

    /// <summary>Stages links a change to a run records, to be saved with it.</summary>
    void AddLinks(IReadOnlyList<WorkflowRunLink> links);
}

/// <summary>A run as stored: the row, its step rows and its links, links oldest first.</summary>
public sealed record WorkflowRunRecord(WorkflowRun Run, IReadOnlyList<WorkflowStepRun> Steps, IReadOnlyList<WorkflowRunLink> Links);

/// <remarks>
/// Relies on the workspace query filter and the ownership interceptor: nothing here names a workspace, and a row
/// stamped for one cannot hang off another's run because the composite keys refuse it.
/// </remarks>
internal sealed class WorkflowRunRepository(CreatorPantryDbContext context) : IWorkflowRunRepository
{
    public void Add(WorkflowRun run, IReadOnlyList<WorkflowStepRun> steps, IReadOnlyList<WorkflowRunLink> links)
    {
        context.WorkflowRuns.Add(run);
        context.WorkflowStepRuns.AddRange(steps);
        context.WorkflowRunLinks.AddRange(links);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => context.SaveChangesAsync(cancellationToken);

    /// <remarks>
    /// Untracked throughout: a read changes nothing, and a tracked row is one stray <c>SaveChanges</c> away from
    /// doing so. Another workspace's run is not found by the filter, exactly as an unknown id is not.
    /// </remarks>
    public async Task<WorkflowRunRecord?> FindAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await context.WorkflowRuns.AsNoTracking().SingleOrDefaultAsync(row => row.Id == runId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        var steps = await context.WorkflowStepRuns.AsNoTracking()
            .Where(step => step.WorkflowRunId == runId)
            .ToListAsync(cancellationToken);
        var links = await context.WorkflowRunLinks.AsNoTracking()
            .Where(link => link.WorkflowRunId == runId)
            .ToListAsync(cancellationToken);

        // Ordered here rather than in SQL: a run holds a handful of links, and SQLite cannot order a DateTimeOffset.
        return new WorkflowRunRecord(run, steps, [.. links.OrderBy(link => link.LinkedAt).ThenBy(link => link.Id)]);
    }

    public async Task<WorkflowRunRecord?> FindForUpdateAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await context.WorkflowRuns.SingleOrDefaultAsync(row => row.Id == runId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        var steps = await context.WorkflowStepRuns.Where(step => step.WorkflowRunId == runId).ToListAsync(cancellationToken);
        var links = await context.WorkflowRunLinks
            .Where(link => link.WorkflowRunId == runId)
            .ToListAsync(cancellationToken);

        return new WorkflowRunRecord(run, steps, [.. links.OrderBy(link => link.LinkedAt).ThenBy(link => link.Id)]);
    }

    public Task SaveChangesAsync(WorkflowRun run, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        // The version the caller read, not the one this context loaded: the UPDATE is conditional on it, so a write
        // that landed between their read and this save makes this one fail instead of silently winning.
        context.Entry(run).Property(row => row.RowVersion).OriginalValue = expectedRowVersion;

        return context.SaveChangesAsync(cancellationToken);
    }

    public void Forget() => context.ChangeTracker.Clear();

    public void AddLinks(IReadOnlyList<WorkflowRunLink> links) => context.WorkflowRunLinks.AddRange(links);
}
