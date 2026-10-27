using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.AiUsage.Data;

/// <summary>
/// EF access to the platform's daily image spend counter (AP.2.7). Every write is one guarded statement.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A <c>BulkOperationBoundaryTests</c> exemption, and the reason is the counter itself.</strong> The
/// ceiling is only a ceiling if "add this picture's price, unless that passes the line" happens in one step that
/// two instances cannot both win — and that is exactly what a conditional <c>UPDATE … WHERE AdmittedMicros + @x
/// &lt;= @ceiling</c> is. The tracked alternative is a row version on one row every account in the platform
/// writes to, where a burst of admissions would exhaust its retries and refuse creators for contention rather
/// than for spend. The account period accepted that trade because its row is one account's; this row is
/// everybody's.
/// </para>
/// <para>
/// What the ban protects does not reach this table. <see cref="PlatformImageSpendDay"/> has no
/// <c>WorkspaceId</c> for <c>WorkspaceOwnershipInterceptor</c> to guard and is not an <c>IImmutableRecord</c>;
/// there is no query filter to ignore and nothing here names one. The statements touch only this one table, by
/// its key.
/// </para>
/// </remarks>
internal interface IPlatformImageSpendRepository
{
    /// <summary>The day's row, untracked, or null when nothing has been admitted on it.</summary>
    Task<PlatformImageSpendDay?> FindAsync(DateOnly day, CancellationToken cancellationToken);

    /// <summary>Creates the day's row at nought if it does not exist yet. Commits on its own.</summary>
    /// <remarks>
    /// Two instances creating it at once collide on the key; the loser's insert is discarded and the winner's
    /// row is the answer, so either way the row exists afterwards.
    /// </remarks>
    Task EnsureAsync(DateOnly day, CancellationToken cancellationToken);

    /// <summary>
    /// Adds <paramref name="micros"/> to the day if the day is lifted, or if it has not tripped and the total stays
    /// at or under <paramref name="ceilingMicros"/>. True when it was added.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A breaker, not a cap.</strong> Once a day has tripped it admits nothing more until it is lifted or
    /// the UTC day ends — not even a smaller request that would still fit under the line — which is what the
    /// trip's alert tells an operator and what "lift the breaker" means.
    /// </para>
    /// <para>
    /// Runs on the caller's transaction when there is one, which is how the charge and the account hold commit
    /// together. The row lock it takes is held until that transaction ends, so the next admission waits for this
    /// one's decision rather than reading the same total.
    /// </para>
    /// </remarks>
    Task<bool> TryChargeAsync(DateOnly day, long micros, long ceilingMicros, CancellationToken cancellationToken);

    /// <summary>Takes back what a never-queued request added. Never takes the total below nought.</summary>
    Task ReturnAsync(DateOnly day, long micros, CancellationToken cancellationToken);

    /// <summary>Marks the day tripped. True for the one caller that did it; false if it already was, or was lifted.</summary>
    Task<bool> TryTripAsync(DateOnly day, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Lifts the day. True for the one caller that did it; false if it already was.</summary>
    Task<bool> TryLiftAsync(DateOnly day, DateTimeOffset now, string liftedBy, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPlatformImageSpendRepository"/>
internal sealed class PlatformImageSpendRepository(CreatorPantryDbContext context) : IPlatformImageSpendRepository
{
    public Task<PlatformImageSpendDay?> FindAsync(DateOnly day, CancellationToken cancellationToken) =>
        context.PlatformImageSpendDays
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Day == day, cancellationToken);

    public async Task EnsureAsync(DateOnly day, CancellationToken cancellationToken)
    {
        if (await context.PlatformImageSpendDays.AnyAsync(row => row.Day == day, cancellationToken))
        {
            return;
        }

        var row = context.PlatformImageSpendDays.Add(new PlatformImageSpendDay { Day = day, AdmittedMicros = 0 });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another instance created it between the read and the insert, and its row is the one that counts —
            // which is only what happened if that row is now there. Anything else that failed this insert is a
            // real fault, and swallowing it would turn every admission of the day into "over the ceiling".
            row.State = EntityState.Detached;

            if (!await context.PlatformImageSpendDays.AnyAsync(existing => existing.Day == day, cancellationToken))
            {
                throw;
            }
        }
        finally
        {
            // Detached either way: the row is written by statement from here on, and a tracked copy would go
            // stale at the first charge and be re-sent by the next SaveChanges on this scope.
            row.State = EntityState.Detached;
        }
    }

    public async Task<bool> TryChargeAsync(
        DateOnly day, long micros, long ceilingMicros, CancellationToken cancellationToken) =>
        await context.PlatformImageSpendDays
            .Where(row => row.Day == day
                && (row.LiftedAt != null
                    || (row.TrippedAt == null && row.AdmittedMicros + micros <= ceilingMicros)))
            .ExecuteUpdateAsync(
                set => set.SetProperty(row => row.AdmittedMicros, row => row.AdmittedMicros + micros),
                cancellationToken) == 1;

    public async Task ReturnAsync(DateOnly day, long micros, CancellationToken cancellationToken) =>
        await context.PlatformImageSpendDays
            .Where(row => row.Day == day && row.AdmittedMicros >= micros)
            .ExecuteUpdateAsync(
                set => set.SetProperty(row => row.AdmittedMicros, row => row.AdmittedMicros - micros),
                cancellationToken);

    public async Task<bool> TryTripAsync(DateOnly day, DateTimeOffset now, CancellationToken cancellationToken) =>
        await context.PlatformImageSpendDays
            .Where(row => row.Day == day && row.TrippedAt == null && row.LiftedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.TrippedAt, (DateTimeOffset?)now), cancellationToken) == 1;

    public async Task<bool> TryLiftAsync(
        DateOnly day, DateTimeOffset now, string liftedBy, CancellationToken cancellationToken) =>
        await context.PlatformImageSpendDays
            .Where(row => row.Day == day && row.LiftedAt == null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(row => row.LiftedAt, (DateTimeOffset?)now)
                    .SetProperty(row => row.LiftedBy, liftedBy),
                cancellationToken) == 1;
}
