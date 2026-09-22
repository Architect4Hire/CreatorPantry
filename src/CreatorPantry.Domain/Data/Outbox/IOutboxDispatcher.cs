namespace CreatorPantry.Domain.Data.Outbox;

public sealed record OutboxDispatchSummary(int Claimed, int Completed, int Retrying, int Poisoned);

public interface IOutboxDispatcher
{
    /// <summary>
    /// Claims and delivers one batch of due messages: newly available ones, and ones whose lease expired
    /// (a prior dispatch pass or process died mid-delivery). Each message's outcome (Completed, retried with
    /// backoff, or Poisoned once attempts are exhausted) is saved as soon as that message is handled, so one
    /// message's exception never loses another message's already-recorded outcome in the same batch.
    /// </summary>
    /// <remarks>
    /// Every Pending and every Leased row is fetched to apply the due/lease-expiry check client-side (not
    /// every EF Core provider translates <c>DateTimeOffset</c> comparison or ordering into SQL), then only
    /// the earliest <see cref="OutboxPolicy.BatchSize"/> due ones are claimed. Correct at any backlog size;
    /// the per-pass read grows with the Pending+Leased backlog. Revisit if that backlog ever grows large
    /// enough for this to matter — nothing today produces one.
    /// </remarks>
    Task<OutboxDispatchSummary> DispatchDueAsync(CancellationToken cancellationToken);
}
