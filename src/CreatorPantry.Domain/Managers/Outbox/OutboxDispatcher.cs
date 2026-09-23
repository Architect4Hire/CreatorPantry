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

        foreach (var message in due)
        {
            message.Status = OutboxMessageStatus.Leased;
            message.LeasedBy = leaseToken;
            message.LeaseExpiresAt = leaseExpiresAt;
        }

        await context.SaveChangesAsync(cancellationToken);

        var completed = 0;
        var retrying = 0;
        var poisoned = 0;

        foreach (var message in due)
        {
            message.Attempts++;

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
                completed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.LastError = Truncate(ex.Message);
                message.LeasedBy = null;
                message.LeaseExpiresAt = null;

                if (message.Attempts >= OutboxPolicy.MaxAttempts)
                {
                    message.Status = OutboxMessageStatus.Poisoned;
                    poisoned++;
                }
                else
                {
                    message.Status = OutboxMessageStatus.Pending;
                    message.AvailableAt = clock.UtcNow + OutboxPolicy.BackoffFor(message.Attempts);
                    retrying++;
                }
            }

            // Saved per message: one message's exception must not roll back another message's already-decided outcome.
            await context.SaveChangesAsync(cancellationToken);
        }

        return new OutboxDispatchSummary(due.Count, completed, retrying, poisoned);
    }

    private static string Truncate(string value) =>
        value.Length > OutboxPolicy.LastErrorMaxLength ? value[..OutboxPolicy.LastErrorMaxLength] : value;
}
