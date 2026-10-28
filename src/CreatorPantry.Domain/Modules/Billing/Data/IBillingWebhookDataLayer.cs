using System.Text.Json;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Gateways;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Billing.Data;

/// <summary>Whether a verified event was new, or one already stored.</summary>
internal enum BillingEventRecording
{
    Recorded = 1,
    Replayed = 2,
}

/// <summary>
/// Composes the webhook's persistence with the provider calls it needs (AP.6.2): verifying a delivery, storing it
/// with its queued work in one transaction, reading a subscription again, and keeping the subscription record.
/// </summary>
internal interface IBillingWebhookDataLayer
{
    /// <summary>Which of the provider's modes this host acts in.</summary>
    BillingPaymentMode Mode { get; }

    BillingWebhookVerificationResult Verify(string? signatureHeader, ReadOnlySpan<byte> body);

    /// <summary>
    /// Stores the verified <paramref name="notice"/> as an event row in <paramref name="status"/> and, when it is
    /// <see cref="BillingProviderEventStatus.Received"/>, the outbox message that processes it — one save, so the event
    /// and its work commit together or neither does (backend.md: outbox).
    /// </summary>
    /// <returns>
    /// <see cref="BillingEventRecording.Replayed"/> when the event id is already stored, found first or lost to a
    /// racing delivery on the unique index: nothing is stored or queued twice.
    /// </returns>
    /// <exception cref="DbUpdateException">The save failed for any reason but that race: nothing was stored.</exception>
    Task<BillingEventRecording> RecordAsync(
        BillingProviderEventNotice notice,
        BillingProviderEventStatus status,
        string? detail,
        Guid correlationId,
        CancellationToken cancellationToken);

    Task<BillingProviderEvent?> FindEventAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Records how processing an event ended. A row already settled is left as it is.</summary>
    Task CompleteAsync(
        Guid id,
        BillingProviderEventStatus status,
        string? accountId,
        string detail,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken);

    Task<BillingSubscriptionRead> ReadSubscriptionAsync(string externalSubscriptionId, CancellationToken cancellationToken);

    /// <summary>The account a provider customer is, in either mode; null when no account is.</summary>
    Task<BillingCustomer?> FindCustomerAsync(string externalCustomerId, CancellationToken cancellationToken);

    /// <summary>
    /// The subscription record's row version, read <em>before</em> the provider is asked — or null when there is no
    /// record yet — so <see cref="RefreshAsync"/> can tell a reading that went stale while it was in flight.
    /// </summary>
    Task<byte[]?> ReadSubscriptionVersionAsync(string externalSubscriptionId, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="snapshot"/> onto the subscription's record — made the first time it is seen — and returns
    /// every subscription the account has in that mode, the written one as it now stands.
    /// </summary>
    /// <param name="versionBeforeRead">What <see cref="ReadSubscriptionVersionAsync"/> said before the provider was asked.</param>
    /// <returns>
    /// Null when another Worker wrote the same subscription since <paramref name="versionBeforeRead"/>: this reading of
    /// the provider may be older than theirs, so it is not written, and the outbox's retry reads the provider again.
    /// </returns>
    Task<IReadOnlyList<BillingSubscription>?> RefreshAsync(
        string accountId,
        BillingSubscriptionSnapshot snapshot,
        byte[]? versionBeforeRead,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether the account's subscription records are still exactly <paramref name="decidedFrom"/> — none added, none
    /// written since. False means another Worker refreshed one while this one decided, and the decision is retried.
    /// </summary>
    Task<bool> StillAsDecidedAsync(
        string accountId, bool isLiveMode, IReadOnlyList<BillingSubscription> decidedFrom, CancellationToken cancellationToken);

    /// <summary>Events left unprocessed since before <paramref name="before"/>, oldest first (AP.6.2's sweep).</summary>
    Task<IReadOnlyList<BillingProviderEvent>> ListStrandedAsync(
        DateTimeOffset before, int take, CancellationToken cancellationToken);

    /// <summary>
    /// Queues a stranded event's work again and counts it, in one save. False when it has been settled meanwhile.
    /// </summary>
    Task<bool> RequeueAsync(Guid id, DateTimeOffset now, Guid correlationId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBillingWebhookDataLayer"/>
internal sealed class BillingWebhookDataLayer(
    IBillingWebhookRepository repository,
    IBillingCustomerRepository customers,
    IBillingPaymentGateway gateway,
    IBillingWebhookVerifier verifier,
    IOutboxWriter outbox,
    IClock clock) : IBillingWebhookDataLayer
{
    public BillingPaymentMode Mode => gateway.Mode;

    public BillingWebhookVerificationResult Verify(string? signatureHeader, ReadOnlySpan<byte> body) =>
        verifier.Verify(signatureHeader, body);

    public async Task<BillingEventRecording> RecordAsync(
        BillingProviderEventNotice notice,
        BillingProviderEventStatus status,
        string? detail,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notice);

        // The common replay — Stripe's own retry of an event this host already answered — is found by a read, so it
        // costs no failed insert. Two deliveries racing past the read meet the unique index below.
        if (await repository.EventExistsAsync(notice.ExternalEventId, cancellationToken))
        {
            return BillingEventRecording.Replayed;
        }

        var now = clock.UtcNow;
        var queue = status == BillingProviderEventStatus.Received;
        var providerEvent = new BillingProviderEvent
        {
            Id = Guid.NewGuid(),
            ExternalEventId = notice.ExternalEventId,
            EventType = notice.EventType,

            // Stored so processing, which happens later, tells a pack's checkout from a reversed charge without
            // reading the provider's type string (AP.6.3).
            Kind = notice.Kind,
            IsLiveMode = notice.IsLiveMode,
            OccurredAt = notice.OccurredAt,
            ReceivedAt = now,
            ObjectReference = notice.ObjectReference,
            SubscriptionReference = notice.SubscriptionReference,
            Status = status,
            Detail = detail,

            // One the route settles itself — ignored, deferred — is processed as it is stored.
            ProcessedAt = queue ? null : now,
        };

        repository.AddEvent(providerEvent);

        if (queue)
        {
            // The row's id only: the handler reads everything else back, and nothing the provider sent rides on the
            // message (IOutboxMessageHandler: never a secret, and here not a byte more than it needs).
            outbox.Enqueue(
                BillingWebhookPolicy.OutboxMessageType,
                JsonSerializer.Serialize(new BillingProviderEventMessage(providerEvent.Id)),
                correlationId);
        }

        try
        {
            await repository.SaveAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Both the event and its message are staged on the shared context; forgetting both is the point — the
            // delivery that won has queued the work already.
            repository.Forget();

            // A replay only if the row is there now. Anything else — an outage, a constraint nobody expected — is
            // rethrown, so the route answers 5xx and Stripe redelivers, rather than 200 for an event nothing stored.
            if (await repository.EventExistsAsync(providerEvent.ExternalEventId, cancellationToken))
            {
                return BillingEventRecording.Replayed;
            }

            throw;
        }

        return BillingEventRecording.Recorded;
    }

    public Task<BillingProviderEvent?> FindEventAsync(Guid id, CancellationToken cancellationToken) =>
        repository.FindEventAsync(id, cancellationToken);

    public async Task CompleteAsync(
        Guid id,
        BillingProviderEventStatus status,
        string? accountId,
        string detail,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken)
    {
        // Re-read rather than kept from the start of processing: a plan write that lost a race forgets everything
        // tracked on the shared context, which would quietly drop an update made to a row held since then.
        var row = await repository.FindEventForUpdateAsync(id, cancellationToken);
        if (row is null || row.Status != BillingProviderEventStatus.Received)
        {
            return;
        }

        row.Status = status;
        row.AccountId = accountId;
        row.Detail = detail;
        row.ProcessedAt = processedAt;

        await repository.SaveAsync(cancellationToken);
    }

    public Task<BillingSubscriptionRead> ReadSubscriptionAsync(
        string externalSubscriptionId, CancellationToken cancellationToken) =>
        gateway.GetSubscriptionAsync(externalSubscriptionId, cancellationToken);

    public Task<BillingCustomer?> FindCustomerAsync(string externalCustomerId, CancellationToken cancellationToken) =>
        customers.FindByExternalCustomerAsync(externalCustomerId, cancellationToken);

    public Task<byte[]?> ReadSubscriptionVersionAsync(string externalSubscriptionId, CancellationToken cancellationToken) =>
        repository.FindSubscriptionVersionAsync(externalSubscriptionId, cancellationToken);

    public async Task<IReadOnlyList<BillingSubscription>?> RefreshAsync(
        string accountId,
        BillingSubscriptionSnapshot snapshot,
        byte[]? versionBeforeRead,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var row = await repository.FindSubscriptionForUpdateAsync(snapshot.ExternalSubscriptionId, cancellationToken);

        // Written since this Worker asked the provider — or made, when there was nothing before: the other Worker's
        // reading may be the newer one, and writing this one over it is how a cancelled subscription could be put back.
        // The row version on the save below covers a write between this load and that save.
        var moved = row is null
            ? versionBeforeRead is not null
            : versionBeforeRead is null || !row.RowVersion.AsSpan().SequenceEqual(versionBeforeRead);

        if (moved)
        {
            repository.Forget();
            return null;
        }

        if (row is null)
        {
            row = new BillingSubscription
            {
                Id = Guid.NewGuid(),
                ExternalSubscriptionId = snapshot.ExternalSubscriptionId,
                CreatedAt = now,
            };

            repository.AddSubscription(row);
        }

        row.AccountId = accountId;
        row.ExternalCustomerId = snapshot.ExternalCustomerId;
        row.IsLiveMode = snapshot.IsLiveMode;
        row.Status = snapshot.Status;
        row.ItemId = snapshot.ItemId;
        row.PlanKey = snapshot.PlanKey;
        row.Cycle = snapshot.Cycle;
        row.CurrentPeriodEnd = snapshot.CurrentPeriodEnd;
        row.CancelAt = snapshot.CancelAt;
        row.EndedAt = snapshot.EndedAt;
        row.ScheduleId = snapshot.ScheduleId;
        row.NextPlanKey = snapshot.NextPhase?.PlanKey;
        row.NextCycle = snapshot.NextPhase?.Cycle ?? BillingCycle.Unspecified;
        row.NextPhaseStartsAt = snapshot.NextPhase?.StartsAt;
        row.LatestInvoicePaid = snapshot.LatestInvoicePaid;
        row.UpdatedAt = now;

        try
        {
            await repository.SaveAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The row version moved: another Worker refreshed this subscription first. Forgotten, then handed back.
            repository.Forget();
            return null;
        }
        catch (DbUpdateException exception) when (IsLostRace(exception))
        {
            // The unique index refused a second first row for one subscription, for the same reason.
            repository.Forget();
            return null;
        }

        return await repository.ListSubscriptionsAsync(accountId, snapshot.IsLiveMode, cancellationToken);
    }

    public async Task<bool> StillAsDecidedAsync(
        string accountId, bool isLiveMode, IReadOnlyList<BillingSubscription> decidedFrom, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decidedFrom);

        var current = await repository.ListSubscriptionsAsync(accountId, isLiveMode, cancellationToken);

        return current.Count == decidedFrom.Count
            && current.All(now => decidedFrom.Any(decided =>
                decided.Id == now.Id && decided.RowVersion.AsSpan().SequenceEqual(now.RowVersion)));
    }

    public Task<IReadOnlyList<BillingProviderEvent>> ListStrandedAsync(
        DateTimeOffset before, int take, CancellationToken cancellationToken) =>
        repository.ListStrandedAsync(before, take, cancellationToken);

    public async Task<bool> RequeueAsync(Guid id, DateTimeOffset now, Guid correlationId, CancellationToken cancellationToken)
    {
        var row = await repository.FindEventForUpdateAsync(id, cancellationToken);
        if (row is null || row.Status != BillingProviderEventStatus.Received)
        {
            return false;
        }

        row.Redrives++;
        row.RedrivenAt = now;

        // The count and the message commit together, so a sweep that dies between them neither loses the message
        // nor counts one that was never queued.
        outbox.Enqueue(
            BillingWebhookPolicy.OutboxMessageType,
            JsonSerializer.Serialize(new BillingProviderEventMessage(row.Id)),
            correlationId);

        await repository.SaveAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// The unique index on the event id (2601, 2627). A provider other than SQL Server — the SQLite the tests run on —
    /// is read as a race throughout, as the plan's append reads it.
    /// </summary>
    private static bool IsLostRace(DbUpdateException exception) =>
        exception.InnerException is not SqlException sql || sql.Number is 2601 or 2627;
}

/// <summary>The outbox payload for one stored provider event: its row id, and nothing else.</summary>
internal sealed record BillingProviderEventMessage(Guid EventId)
{
    public static BillingProviderEventMessage? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<BillingProviderEventMessage>(json) is { EventId: var id } message && id != Guid.Empty
                ? message
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
