using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Managers.Outbox;

internal sealed class OutboxDispatcher(CreatorPantryDbContext context, IServiceProvider handlers, IClock clock) : IOutboxDispatcher
{
    public async Task<OutboxDispatchSummary> DispatchDueAsync(CancellationToken cancellationToken)
    {
        var claimedAt = clock.UtcNow;
        var leaseToken = Guid.NewGuid();
        var leaseExpiresAt = claimedAt + OutboxPolicy.LeaseDuration;

        // Claimable: newly due Pending rows, or Leased rows whose lease expired (the dispatch pass or
        // process that held them died before recording an outcome) — the crash-recovery/restart path.
        // Filtering and ordering by the DateTimeOffset columns happens client-side, not in SQL: not every
        // EF Core provider translates DateTimeOffset comparisons (see IdempotencyRepository for the same
        // workaround), and SQLite's provider specifically cannot even translate ORDER BY on one. Only the
        // Status filter — translatable on every provider — runs server-side.
        var pendingCandidates = await context.OutboxMessages
            .Where(message => message.Status == OutboxMessageStatus.Pending)
            .ToListAsync(cancellationToken);
        var leasedCandidates = await context.OutboxMessages
            .Where(message => message.Status == OutboxMessageStatus.Leased)
            .ToListAsync(cancellationToken);

        var due = pendingCandidates.Where(message => message.AvailableAt <= claimedAt)
            .Concat(leasedCandidates.Where(message => message.LeaseExpiresAt < claimedAt))
            .OrderBy(message => message.AvailableAt)
            .Take(OutboxPolicy.BatchSize)
            .ToList();

        if (due.Count == 0)
        {
            return new OutboxDispatchSummary(0, 0, 0, 0);
        }

        // Claimed one row at a time, each claim its own UPDATE guarded by the state this pass read (12.10l).
        // Status and LeasedBy are concurrency tokens, so the UPDATE names both in its WHERE clause: if another
        // dispatcher claimed the row between the read above and this write, it matches nothing, EF raises
        // DbUpdateConcurrencyException, and this pass leaves the message to whoever won. Without that guard two
        // Worker instances that read the same due row would both dispatch it — harmless for a handler that is
        // idempotent, and a double delivery for the first one that calls anything outside this database.
        var claimed = new List<OutboxMessage>(due.Count);

        foreach (var message in due)
        {
            message.Status = OutboxMessageStatus.Leased;
            message.LeasedBy = leaseToken;
            message.LeaseExpiresAt = leaseExpiresAt;

            try
            {
                await context.SaveChangesAsync(cancellationToken);
                claimed.Add(message);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Lost the race. Detached rather than reverted: the row is no longer in the state this pass
                // read, so there is nothing true left to track, and a still-Modified entity would be written
                // again by the next save.
                context.Entry(message).State = EntityState.Detached;
            }
        }

        var completed = 0;
        var retrying = 0;
        var poisoned = 0;

        foreach (var message in claimed)
        {
            message.Attempts++;
            Outcome outcome;

            try
            {
                var handler = handlers.GetKeyedService<IOutboxMessageHandler>(message.Type)
                    ?? throw new InvalidOperationException($"No outbox handler is registered for message type '{message.Type}'.");

                await handler.HandleAsync(
                    new OutboxMessageEnvelope(message.Id, message.Type, message.PayloadJson, message.CorrelationId, message.Attempts),
                    cancellationToken);

                message.Status = OutboxMessageStatus.Completed;
                message.CompletedAt = clock.UtcNow;
                message.LeasedBy = null;
                message.LeaseExpiresAt = null;
                outcome = Outcome.Completed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.LastError = Truncate(ex.Message);
                message.LeasedBy = null;
                message.LeaseExpiresAt = null;

                if (message.Attempts >= OutboxPolicy.MaxAttempts)
                {
                    message.Status = OutboxMessageStatus.Poisoned;
                    outcome = Outcome.Poisoned;
                }
                else
                {
                    message.Status = OutboxMessageStatus.Pending;
                    message.AvailableAt = clock.UtcNow + OutboxPolicy.BackoffFor(message.Attempts);
                    outcome = Outcome.Retrying;
                }
            }

            // Saved per message: one message's exception must not roll back another message's already-decided outcome.
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // The lease ran out while the handler worked and another dispatcher has taken the row: the same
                // guard as the claim, quoting this pass's own lease token. The outcome is theirs to record now,
                // so this one is dropped rather than written over it.
                context.Entry(message).State = EntityState.Detached;
                continue;
            }

            switch (outcome)
            {
                case Outcome.Completed:
                    completed++;
                    break;
                case Outcome.Poisoned:
                    poisoned++;
                    break;
                default:
                    retrying++;
                    break;
            }
        }

        return new OutboxDispatchSummary(claimed.Count, completed, retrying, poisoned);
    }

    private enum Outcome
    {
        Completed,
        Retrying,
        Poisoned,
    }

    private static string Truncate(string value) =>
        value.Length > OutboxPolicy.LastErrorMaxLength ? value[..OutboxPolicy.LastErrorMaxLength] : value;
}
