using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Content.Data;

public interface IWorkspaceWeeklyThemeDataLayer
{
    /// <inheritdoc cref="IWorkspaceWeeklyThemeRepository.ListAsync"/>
    Task<List<WorkspaceWeeklyTheme>> ListAsync(CancellationToken cancellationToken);

    /// <inheritdoc cref="IWorkspaceWeeklyThemeRepository.FindAsync"/>
    Task<WorkspaceWeeklyTheme?> FindAsync(string key, CancellationToken cancellationToken);

    /// <inheritdoc cref="IWorkspaceWeeklyThemeRepository.FindForUpdateAsync"/>
    Task<WorkspaceWeeklyTheme?> FindForUpdateAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a plan and its audit entry as one unit. False when the write lost a race to another replace,
    /// which the unique indexes decide; nothing is written and nothing is left staged.
    /// </summary>
    Task<bool> ReplaceAsync(WeeklyThemeWeekPlan plan, AuditEntry audit, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes one theme and writes its audit entry as one unit. False when the row went away first, which is
    /// the same answer a caller gets for a key the workspace never had.
    /// </summary>
    Task<bool> DeleteAsync(WorkspaceWeeklyTheme theme, AuditEntry audit, CancellationToken cancellationToken);
}

internal sealed class WorkspaceWeeklyThemeDataLayer(
    IWorkspaceWeeklyThemeRepository themes,
    IAuditWriter auditWriter,
    CreatorPantryDbContext context) : IWorkspaceWeeklyThemeDataLayer
{
    public Task<List<WorkspaceWeeklyTheme>> ListAsync(CancellationToken cancellationToken) =>
        themes.ListAsync(cancellationToken);

    public Task<WorkspaceWeeklyTheme?> FindAsync(string key, CancellationToken cancellationToken) =>
        themes.FindAsync(key, cancellationToken);

    public Task<WorkspaceWeeklyTheme?> FindForUpdateAsync(string key, CancellationToken cancellationToken) =>
        themes.FindForUpdateAsync(key, cancellationToken);

    /// <summary>
    /// The transaction boundary for a replace, which — unusually for this codebase — genuinely needs one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why two batches.</strong> A day that changes hands has to be released before anything claims it.
    /// <c>UX_WorkspaceWeeklyThemes_Workspace_Day</c> covers live rows, so a single <c>SaveChangesAsync</c>
    /// containing both the insert of Monday's new theme and the retirement of its old one violates the index if
    /// the insert reaches the server first — and EF's statement ordering within a table is not a contract to
    /// build on. Swapping two themes between two days is the same problem with two updates and no ordering to
    /// appeal to at all. So this releases first, then places, and the index is left to catch a real second theme
    /// on a Monday rather than a half-applied replace.
    /// </para>
    /// <para>
    /// <strong>Why it joins an existing transaction.</strong> A request carrying an idempotency key already runs
    /// inside <c>IdempotencyDataLayer</c>'s transaction, so this must not start a nested one. That transaction
    /// also holds the idempotency record on the change tracker, which is why nothing on the success path here
    /// clears the tracker — detaching that record would lose the result the executor is about to save. The
    /// failure paths do clear it, and may: a failure returns <c>false</c>, which becomes a conflict, and the
    /// executor rolls its whole transaction back rather than saving anything. A request without a key has no
    /// transaction, so one is opened here, wrapped in the execution strategy because
    /// <c>EnrichSqlServerDbContext</c> enables a retrying one and EF refuses a user-initiated transaction
    /// otherwise. That retry is why <see cref="WeeklyThemeWeekPlan"/> is values rather than staged mutations:
    /// each attempt starts from a clean tracker and re-reads the week.
    /// </para>
    /// </remarks>
    public Task<bool> ReplaceAsync(WeeklyThemeWeekPlan plan, AuditEntry audit, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is not null)
        {
            return ApplyAsync(plan, audit, cancellationToken);
        }

        return context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            if (!await ApplyAsync(plan, audit, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);

                return false;
            }

            await transaction.CommitAsync(cancellationToken);

            return true;
        });
    }

    public async Task<bool> DeleteAsync(
        WorkspaceWeeklyTheme theme, AuditEntry audit, CancellationToken cancellationToken)
    {
        themes.Remove(theme);
        auditWriter.Record(audit);

        try
        {
            // One save, one transaction: the row and the record that it went commit together or not at all.
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else deleted it between the read and here. Nothing committed, and nothing may stay staged.
            context.ChangeTracker.Clear();

            return false;
        }

        return true;
    }

    private async Task<bool> ApplyAsync(
        WeeklyThemeWeekPlan plan, AuditEntry audit, CancellationToken cancellationToken)
    {
        var stored = await themes.ListForUpdateAsync(cancellationToken);
        var byKey = stored.ToDictionary(theme => theme.Key, StringComparer.Ordinal);

        // What this attempt is taking, remembered so a failure can be asked about rather than assumed. A key it
        // inserts and a live day it claims are the two things a concurrent replace can take from under it.
        var inserted = new List<string>();
        var claimed = new Dictionary<DayOfWeek, string>();

        try
        {
            // Batch one: release every day that is changing hands. A theme that is only leaving the week reaches
            // its whole final state here, because nothing further happens to it; one that is moving releases its
            // day now and is placed on the new one below.
            var released = false;
            foreach (var change in plan.Changes)
            {
                if (!byKey.TryGetValue(change.Key, out var theme) || theme.RetiredAt is not null)
                {
                    continue;
                }

                if (change.Retire)
                {
                    theme.RetiredAt = plan.At;
                    theme.Revision += 1;
                    theme.UpdatedAt = plan.At;
                    released = true;
                }
                else if (theme.Day != change.Day)
                {
                    theme.RetiredAt = plan.At;
                    released = true;
                }
            }

            if (released)
            {
                await context.SaveChangesAsync(cancellationToken);
            }

            // Batch two: place the week. Every day claimed here is either untouched by batch one or was released
            // by it, and validation has already refused two themes on one day, so nothing can collide.
            foreach (var change in plan.Changes.Where(change => !change.Retire))
            {
                claimed[change.Day] = change.Key;

                if (byKey.TryGetValue(change.Key, out var theme))
                {
                    theme.Day = change.Day;
                    theme.DisplayName = change.DisplayName;
                    theme.Description = change.Description;
                    theme.RetiredAt = null;
                    theme.Revision += 1;
                    theme.UpdatedAt = plan.At;

                    continue;
                }

                inserted.Add(change.Key);

                // WorkspaceId is left unset: stamping it is WorkspaceOwnershipInterceptor's job, from the resolved
                // context, and a value set by hand here would be the only place a client-shaped id could enter.
                themes.Add(new WorkspaceWeeklyTheme
                {
                    Id = Guid.NewGuid(),
                    Day = change.Day,
                    Key = change.Key,
                    DisplayName = change.DisplayName,
                    Description = change.Description,
                    Revision = 1,
                    CreatedAt = plan.At,
                    UpdatedAt = plan.At,
                });
            }

            auditWriter.Record(audit);

            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A row this replace was changing is no longer there to change. Both batches can raise it, which is
            // why they share one handler: a theme deleted between the read and either save is a conflict.
            context.ChangeTracker.Clear();

            return false;
        }
        catch (DbUpdateException)
        {
            // Nothing committed, and nothing may stay staged for a later save on this scope to pick up.
            context.ChangeTracker.Clear();

            // The two unique indexes are the guards against a concurrent replace, but a DbUpdateException is not
            // only ever one of them: a deadlock victim and a command timeout arrive the same way, and answering
            // those with "reload the week and apply your change again" would send a caller round a loop that
            // cannot end differently. So ask the question that matters — has another writer taken something this
            // attempt was placing? — and let anything else go on being an error.
            if (!await LostToAnotherWriterAsync(inserted, claimed, cancellationToken))
            {
                throw;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether the week now holds something this attempt was placing, which is what a conflict means here.
    /// </summary>
    /// <param name="inserted">Keys this attempt was creating. One that exists now was created by someone else.</param>
    /// <param name="claimed">
    /// The live day each placed theme was taking. A day now held by a different theme was taken by someone else.
    /// </param>
    /// <remarks>
    /// Read fresh and workspace-scoped like every other read here, and asked only after the change tracker is
    /// clear, so it sees the committed week rather than this attempt's abandoned intentions.
    /// </remarks>
    private async Task<bool> LostToAnotherWriterAsync(
        IReadOnlyList<string> inserted,
        IReadOnlyDictionary<DayOfWeek, string> claimed,
        CancellationToken cancellationToken)
    {
        if (inserted.Count == 0 && claimed.Count == 0)
        {
            return false;
        }

        var stored = await themes.ListAsync(cancellationToken);

        return inserted.Any(key => stored.Any(theme => theme.Key == key))
            || stored.Any(theme =>
                theme.RetiredAt is null
                && claimed.TryGetValue(theme.Day, out var key)
                && !string.Equals(theme.Key, key, StringComparison.Ordinal));
    }
}
