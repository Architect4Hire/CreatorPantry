using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Workflows.Data;

public interface IWorkflowRunDataLayer
{
    /// <summary>Writes a new run, its step rows and its links as one save, or none of them.</summary>
    Task CreateAsync(
        WorkflowRun run,
        IReadOnlyList<WorkflowStepRun> steps,
        IReadOnlyList<WorkflowRunLink> links,
        CancellationToken cancellationToken);

    Task<WorkflowRunRecord?> GetAsync(Guid runId, CancellationToken cancellationToken);

    Task<WorkflowRunRecord?> GetForUpdateAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>
    /// Stops the tracked old run (against the version the caller read) and writes the new run that replaces it, as
    /// one save: both land or neither does.
    /// </summary>
    Task<WorkflowRunWrite> RestartAsync(
        WorkflowRun oldRun,
        byte[] expectedRowVersion,
        WorkflowRun newRun,
        IReadOnlyList<WorkflowStepRun> newSteps,
        IReadOnlyList<WorkflowRunLink> newLinks,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stops the tracked run against the version the caller read, with its audit row, as one save: the stop and its
    /// record land together or neither does. <see cref="WorkflowRunWrite.Stale"/> when the run changed since.
    /// </summary>
    Task<WorkflowRunWrite> AbandonAsync(
        WorkflowRun run,
        byte[] expectedRowVersion,
        AuditEntry audit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Saves a change to a run — its row and any of its step rows — as one write against the version the caller
    /// read. <see cref="WorkflowRunWrite.Stale"/> when the run changed since; nothing is written then.
    /// </summary>
    Task<WorkflowRunWrite> SaveAsync(
        WorkflowRun run,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken,
        IReadOnlyList<WorkflowRunLink>? newLinks = null);
}

public enum WorkflowRunWrite
{
    Saved = 1,

    /// <summary>The run changed after the caller read it.</summary>
    Stale = 2,
}

internal sealed class WorkflowRunDataLayer(IWorkflowRunRepository runs, IAuditWriter audits) : IWorkflowRunDataLayer
{
    /// <remarks>
    /// One <c>SaveChanges</c> is one transaction: a run never exists without the step rows its progress is counted
    /// over, nor a link without its run.
    /// </remarks>
    public async Task CreateAsync(
        WorkflowRun run,
        IReadOnlyList<WorkflowStepRun> steps,
        IReadOnlyList<WorkflowRunLink> links,
        CancellationToken cancellationToken)
    {
        runs.Add(run, steps, links);
        await runs.SaveChangesAsync(cancellationToken);
    }

    public Task<WorkflowRunRecord?> GetAsync(Guid runId, CancellationToken cancellationToken) =>
        runs.FindAsync(runId, cancellationToken);

    public Task<WorkflowRunRecord?> GetForUpdateAsync(Guid runId, CancellationToken cancellationToken) =>
        runs.FindForUpdateAsync(runId, cancellationToken);

    public Task<WorkflowRunWrite> RestartAsync(
        WorkflowRun oldRun,
        byte[] expectedRowVersion,
        WorkflowRun newRun,
        IReadOnlyList<WorkflowStepRun> newSteps,
        IReadOnlyList<WorkflowRunLink> newLinks,
        CancellationToken cancellationToken)
    {
        runs.Add(newRun, newSteps, newLinks);

        return SaveAsync(oldRun, expectedRowVersion, cancellationToken);
    }

    /// <remarks>A save that finds the run changed forgets the staged audit row with everything else it tracked.</remarks>
    public Task<WorkflowRunWrite> AbandonAsync(
        WorkflowRun run,
        byte[] expectedRowVersion,
        AuditEntry audit,
        CancellationToken cancellationToken)
    {
        audits.Record(audit);

        return SaveAsync(run, expectedRowVersion, cancellationToken);
    }

    /// <remarks>Links a change records go in the same save as the run, so they land with it or not at all.</remarks>
    public async Task<WorkflowRunWrite> SaveAsync(
        WorkflowRun run,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken,
        IReadOnlyList<WorkflowRunLink>? newLinks = null)
    {
        try
        {
            if (newLinks is { Count: > 0 })
            {
                runs.AddLinks(newLinks);
            }

            await runs.SaveChangesAsync(run, expectedRowVersion, cancellationToken);

            return WorkflowRunWrite.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            runs.Forget();

            return WorkflowRunWrite.Stale;
        }
    }
}
