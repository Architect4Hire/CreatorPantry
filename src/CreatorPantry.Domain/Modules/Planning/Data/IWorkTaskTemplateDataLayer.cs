using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Planning.Data;

/// <summary>What became of one occurrence's save.</summary>
public enum WorkTaskOccurrenceWrite
{
    /// <summary>The task was made and the routine moved on past its date.</summary>
    Made = 0,

    /// <summary>A task for the date already existed, so none was made; the routine moved on past it.</summary>
    AlreadyMade = 1,

    /// <summary>The routine changed under this pass — another pass, or an edit. Nothing was written; stop.</summary>
    Stale = 2,
}

/// <summary>Complete persistence operations for routines and the occurrences they make (13A.30).</summary>
public interface IWorkTaskTemplateDataLayer
{
    /// <summary>The routine with this id in the resolved workspace, tracked, or null for none and for another workspace's.</summary>
    Task<WorkTaskTemplate?> FindForUpdateAsync(Guid templateId, CancellationToken cancellationToken);

    /// <summary>
    /// Saves one occurrence: <paramref name="task"/>, at the bottom of its day's list, and the tracked routine with
    /// its next date already moved on — together, against the routine's row version.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Never twice.</strong> A task for the routine and date is looked for first; one already there — an
    /// earlier attempt's, or another pass's after a lease lapsed — means none is made and only the routine moves
    /// on. Should one still slip in between the look and the save, the store's one-per-occurrence index refuses
    /// the second, and the save is reported <see cref="WorkTaskOccurrenceWrite.Stale"/> for the next pass to settle.
    /// </para>
    /// <para>
    /// Because the task and the routine's next date are one save, a pass that stops part-way leaves every date
    /// either made and passed or neither, and the next pass carries on from there.
    /// </para>
    /// </remarks>
    Task<WorkTaskOccurrenceWrite> SaveOccurrenceAsync(
        WorkTaskTemplate template, WorkTask task, CancellationToken cancellationToken);

    /// <summary>
    /// Saves the tracked routine alone — its status, its next date, its lease let go. False when it changed under
    /// this pass; a refusal of any other kind is thrown, so a routine that cannot be saved is logged rather than
    /// quietly retried as if it had raced.
    /// </summary>
    Task<bool> SaveTemplateAsync(WorkTaskTemplate template, CancellationToken cancellationToken);
}

internal sealed class WorkTaskTemplateDataLayer(IWorkTaskRepository tasks, CreatorPantryDbContext db) : IWorkTaskTemplateDataLayer
{
    public Task<WorkTaskTemplate?> FindForUpdateAsync(Guid templateId, CancellationToken cancellationToken) =>
        db.WorkTaskTemplates.FirstOrDefaultAsync(template => template.Id == templateId, cancellationToken);

    public async Task<WorkTaskOccurrenceWrite> SaveOccurrenceAsync(
        WorkTaskTemplate template, WorkTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(task);

        var alreadyMade = await db.WorkTasks.AsNoTracking()
            .AnyAsync(row => row.WorkTaskTemplateId == template.Id && row.RecurrenceDate == task.RecurrenceDate, cancellationToken);

        if (!alreadyMade)
        {
            var bottom = await tasks.HighestRankAsync(task.AssigneeMembershipId, task.Status, task.ScheduledDate, cancellationToken);
            task.SortRank = (bottom ?? 0) + Managers.WorkTaskPolicy.RankGap;
            tasks.Add(task);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);

            return alreadyMade ? WorkTaskOccurrenceWrite.AlreadyMade : WorkTaskOccurrenceWrite.Made;
        }
        catch (DbUpdateConcurrencyException)
        {
            // The routine changed under this pass. Nothing of this save landed.
            db.ChangeTracker.Clear();

            return WorkTaskOccurrenceWrite.Stale;
        }
        catch (DbUpdateException)
        {
            // The one-per-occurrence index: another pass made this date between the look and the save. Benign, and
            // the next pass settles it. Any other refusal is not a race and is left to reach the worker's log.
            if (await OccurrenceExistsAsync(template.Id, task.RecurrenceDate, cancellationToken))
            {
                return WorkTaskOccurrenceWrite.Stale;
            }

            throw;
        }
    }

    /// <summary>
    /// Whether the occurrence exists, asked after a refused save — with the tracker cleared first, so the refused
    /// rows are not what answers.
    /// </summary>
    private async Task<bool> OccurrenceExistsAsync(Guid templateId, DateOnly? date, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();

        return await db.WorkTasks.AsNoTracking()
            .AnyAsync(row => row.WorkTaskTemplateId == templateId && row.RecurrenceDate == date, cancellationToken);
    }

    public async Task<bool> SaveTemplateAsync(WorkTaskTemplate template, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(template);

        try
        {
            await db.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Changed under this pass; nothing landed. Anything else is not a race and reaches the worker's log.
            db.ChangeTracker.Clear();

            return false;
        }
    }
}
