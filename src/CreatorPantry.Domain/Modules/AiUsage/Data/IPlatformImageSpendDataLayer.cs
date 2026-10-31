using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.AiUsage.Data;

/// <summary>
/// The image spend breaker's own writes — tripping and lifting — and the operator's read of a day (AP.2.7).
/// </summary>
/// <remarks>
/// <para>
/// The charge itself is not here: it has to commit with the account hold, so it lives in
/// <see cref="IAiQuotaDataLayer.ReserveAsync"/>. What is here are the two state changes that each write a
/// <see cref="PlatformAuditLog"/> row, and each commits that row in the same transaction as the change, or
/// neither — an audit trail that recorded a trip the database never made would be worse than none.
/// </para>
/// <para>
/// Both are guarded statements, so exactly one caller wins each: a burst of refusals raises one trip and one
/// audit row, and a lift retried after a lost response finds the day already lifted and writes nothing.
/// </para>
/// </remarks>
internal interface IPlatformImageSpendDataLayer
{
    /// <summary>The day's counter, or null when nothing has been admitted on it.</summary>
    Task<PlatformImageSpendDay?> FindDayAsync(DateOnly day, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the day tripped and stages <paramref name="audit"/> with it. True only for the caller that tripped it.
    /// </summary>
    Task<bool> TripAsync(
        DateOnly day, DateTimeOffset now, PlatformAuditEntry audit, CancellationToken cancellationToken);

    /// <summary>
    /// Lifts the day for <paramref name="liftedBy"/> and stages <paramref name="audit"/> with it. True only for
    /// the caller that lifted it; false, with nothing written, when it already was.
    /// </summary>
    Task<bool> LiftAsync(
        DateOnly day,
        DateTimeOffset now,
        string liftedBy,
        PlatformAuditEntry audit,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPlatformImageSpendDataLayer"/>
internal sealed class PlatformImageSpendDataLayer(
    IPlatformImageSpendRepository spend,
    IPlatformAuditWriter auditWriter,
    CreatorPantryDbContext context) : IPlatformImageSpendDataLayer
{
    public Task<PlatformImageSpendDay?> FindDayAsync(DateOnly day, CancellationToken cancellationToken) =>
        spend.FindAsync(day, cancellationToken);

    public Task<bool> TripAsync(
        DateOnly day, DateTimeOffset now, PlatformAuditEntry audit, CancellationToken cancellationToken) =>
        GuardedWithAuditAsync(
            token => spend.TryTripAsync(day, now, token), audit, cancellationToken);

    /// <remarks>
    /// The day's row is created first if no picture has been admitted yet, so an operator can lift a day
    /// before its first request — ahead of a launch, say — rather than only after it has tripped.
    /// </remarks>
    public async Task<bool> LiftAsync(
        DateOnly day,
        DateTimeOffset now,
        string liftedBy,
        PlatformAuditEntry audit,
        CancellationToken cancellationToken)
    {
        await spend.EnsureAsync(day, cancellationToken);

        return await GuardedWithAuditAsync(
            token => spend.TryLiftAsync(day, now, liftedBy, token), audit, cancellationToken);
    }

    /// <summary>
    /// One guarded statement and, only if it won, its audit row — in one transaction.
    /// </summary>
    /// <remarks>
    /// Inside an execution strategy for the reason <see cref="AiQuotaDataLayer"/> gives, and from a clean
    /// tracker, so a retried unit never re-sends an audit row staged by the attempt that faulted.
    /// </remarks>
    private Task<bool> GuardedWithAuditAsync(
        Func<CancellationToken, Task<bool>> guarded,
        PlatformAuditEntry audit,
        CancellationToken cancellationToken) =>
        context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();

            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            if (!await guarded(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);

                return false;
            }

            auditWriter.Record(audit);

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return true;
        });
}
