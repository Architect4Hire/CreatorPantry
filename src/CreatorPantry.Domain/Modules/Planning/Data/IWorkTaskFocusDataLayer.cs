using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Planning.Data;

/// <summary>Complete persistence operations for one person's top three (13A.32), and the transaction around them.</summary>
public interface IWorkTaskFocusDataLayer
{
    /// <inheritdoc cref="IWorkTaskFocusRepository.ChosenAsync"/>
    Task<IReadOnlyList<Guid>> ChosenAsync(Guid assigneeMembershipId, DateOnly focusDate, CancellationToken cancellationToken);

    /// <inheritdoc cref="IWorkTaskFocusRepository.TasksAsync"/>
    Task<IReadOnlyList<WorkTask>> TasksAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces one person's top three for one day with <paramref name="taskIds"/>, in that order. False when the
    /// store refused it; nothing was written then, and the request may be sent again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One transaction, two saves.</strong> The day's rows are deleted and saved before the new ones are
    /// inserted, so a task moving from second to first never meets itself in the unique index. Both commit together
    /// or not at all.
    /// </para>
    /// <para>
    /// Two replaces of the same day at once may both delete and both insert; the second insert then breaks the key
    /// and is refused, and the first choice stands. That is the honest answer for one person's two tabs.
    /// </para>
    /// <para>
    /// <strong>Whether a refusal was a race is answered by looking, not by reading the exception</strong>: another
    /// choice for the day now stands, or a named task has gone. Anything else is not something a retry fixes, and
    /// is rethrown to be logged and answered as a server fault rather than as "send it again".
    /// </para>
    /// <para>
    /// It clears the request's change tracker before each attempt, so it must be the only writer in its scope —
    /// as it is: the facade validates and calls it, and nothing else is staged in that request.
    /// </para>
    /// </remarks>
    Task<bool> ReplaceAsync(
        Guid assigneeMembershipId, DateOnly focusDate, IReadOnlyList<Guid> taskIds, CancellationToken cancellationToken);
}

internal sealed class WorkTaskFocusDataLayer(IWorkTaskFocusRepository repository, CreatorPantryDbContext db)
    : IWorkTaskFocusDataLayer
{
    public Task<IReadOnlyList<Guid>> ChosenAsync(
        Guid assigneeMembershipId, DateOnly focusDate, CancellationToken cancellationToken) =>
        repository.ChosenAsync(assigneeMembershipId, focusDate, cancellationToken);

    public Task<IReadOnlyList<WorkTask>> TasksAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken) =>
        repository.TasksAsync(taskIds, cancellationToken);

    public async Task<bool> ReplaceAsync(
        Guid assigneeMembershipId, DateOnly focusDate, IReadOnlyList<Guid> taskIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskIds);

        try
        {
            // The configured retrying strategy refuses a user-initiated transaction outside it. Each attempt starts
            // from a clean tracker and re-reads the day, so a retry never replays half of the last one.
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

                repository.RemoveRange(await repository.RowsForUpdateAsync(assigneeMembershipId, focusDate, cancellationToken));
                await db.SaveChangesAsync(cancellationToken);

                for (var index = 0; index < taskIds.Count; index++)
                {
                    repository.Add(new WorkTaskFocus
                    {
                        AssigneeMembershipId = assigneeMembershipId,
                        FocusDate = focusDate,
                        Position = index + 1,
                        WorkTaskId = taskIds[index],
                    });
                }

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                return true;
            });
        }
        catch (DbUpdateException)
        {
            // The transaction was disposed uncommitted, so nothing of this one landed. Benign only if a race explains
            // it: a replace of the same day that got there first, or a task removed in between.
            db.ChangeTracker.Clear();

            if (await repository.ChosenAsync(assigneeMembershipId, focusDate, cancellationToken) is { Count: > 0 }
                || (await repository.TasksAsync(taskIds, cancellationToken)).Count < taskIds.Count)
            {
                return false;
            }

            throw;
        }
    }
}
