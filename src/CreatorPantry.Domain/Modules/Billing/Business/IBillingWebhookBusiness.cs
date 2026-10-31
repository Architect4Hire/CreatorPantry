using System.Diagnostics;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Billing.Data;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Gateways;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Domain.Modules.Billing.Business;

/// <summary>
/// The payment provider's webhook (PRICE-010, AP.6.2): taking a delivery, and acting on it later from the outbox.
/// </summary>
internal interface IBillingWebhookBusiness
{
    /// <summary>
    /// Verifies a delivery's signature over its raw bytes before anything in it is read, then stores the event and
    /// queues its work in one save. Returns at once; nothing about a plan is decided here.
    /// </summary>
    Task<OperationResult<BillingWebhookReceiptServiceModel>> ReceiveAsync(
        string? signatureHeader, ReadOnlyMemory<byte> body, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the event's subject again from the provider and acts on it: a subscription brings the account's plan into
    /// line with every subscription it has (B-54, B-56, B-57), and a pack's checkout or a reversed payment grants or
    /// claws back credits (B-50, AP.6.3).
    /// </summary>
    /// <exception cref="BillingWebhookRetryException">
    /// The provider could not be read, or the plan chain moved under the change: the outbox retries the message.
    /// </exception>
    Task<BillingProviderEventOutcome> ProcessAsync(Guid eventId, CancellationToken cancellationToken);

    /// <summary>
    /// Queues again every event left unprocessed for longer than <see cref="BillingWebhookPolicy.StrandedAfter"/> — its
    /// message poisoned while the provider could not be read — and leaves one queued
    /// <see cref="BillingWebhookPolicy.MaxRedrives"/> times for an operator (AP.6.2).
    /// </summary>
    /// <remarks>
    /// Needed because the route answered 200: Stripe does not deliver an acknowledged event again, and the last event
    /// about a subscription — its deletion — has no later one to heal it. Queuing an event whose first message is
    /// still being retried does no harm, since processing is idempotent.
    /// </remarks>
    /// <returns>How many were queued again and how many were given up on.</returns>
    Task<(int Requeued, int GivenUp)> RedriveStrandedAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Processing an event could not finish for a reason that may pass — the provider not answering, a lost race — so
/// the outbox should deliver the message again. Carries no provider text.
/// </summary>
internal sealed class BillingWebhookRetryException(string reason) : Exception(reason);

/// <inheritdoc cref="IBillingWebhookBusiness"/>
/// <remarks>
/// <para>
/// <strong>Credits are the other half, and they are a different business's</strong> (AP.6.3): an event about a pack's
/// checkout or a reversed payment is handed to <see cref="IBillingPackPurchaseBusiness"/>, which owns the grant and
/// the claw-back, while this class keeps the event row and the plan. A Business calling a Business in its own module,
/// as the plan chain is reached (backend.md).
/// </para>
/// <para>
/// <strong>What the plan follows is the provider's subscription as it is now, never what an event said.</strong>
/// Stripe does not deliver in order and may deliver twice, so an event only names which subscription to read
/// again. An older event processed after a newer one reads the same current subscription and decides the same
/// thing; a replayed one finds the chain already as wanted and writes nothing.
/// </para>
/// <para>
/// <strong>The transitions</strong> (owner, 2026-10-28, approving AP.6.2):
/// a higher plan lands at once once its invoice is paid — an upgrade's proration still open holds it back — and its
/// credits follow at the next admission (B-56's top-up);
/// a lower plan lands when the period being paid for ends — wherever it was changed, link.com included — but only
/// when the plan in force is one the creator paid for through the provider (B-54 keeps what was paid for, and a
/// default, trial or ops plan was not);
/// a cancellation at period end is scheduled for then; one withdrawn takes its scheduled row back off;
/// a renewal being retried (past due) changes nothing in either direction, so an unpaid upgrade never lands;
/// and a subscription that has ended puts the account back on the configured default at once — only when the plan in
/// force is one a subscription put there, so a late deletion never takes away an ops grant made since.
/// </para>
/// <para>
/// <strong>Two Workers, one account.</strong> A reading of the provider is written only if the record has not moved
/// since before it was asked, and a reconcile stands only if no record moved while it decided; either way the loser
/// is retried and reads the provider again, so the newest reading has the last word.
/// </para>
/// <para>
/// <strong>Decided from every subscription the account has</strong>, not just the one the event is about, so a
/// cancelled subscription's late event cannot lapse an account another subscription is paying for. With two
/// running at once — a second checkout — the higher plan is the account's.
/// </para>
/// <para>
/// <strong>An event that cannot be tied to an account changes nothing.</strong> The subscription's customer must be
/// a <see cref="BillingCustomer"/> in the same mode, and the account its metadata names must be that customer's
/// account; anything else is <see cref="BillingProviderEventStatus.NeedsAttention"/>.
/// </para>
/// </remarks>
internal sealed class BillingWebhookBusiness(
    IBillingWebhookDataLayer data,
    IAccountPlanBusiness plans,
    IBillingPackPurchaseBusiness packs,
    IOptions<BillingOptions> options,
    IClock clock,
    ILogger<BillingWebhookBusiness> logger) : IBillingWebhookBusiness
{
    public async Task<OperationResult<BillingWebhookReceiptServiceModel>> ReceiveAsync(
        string? signatureHeader, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        var verified = data.Verify(signatureHeader, body.Span);

        switch (verified.Outcome)
        {
            case BillingWebhookVerification.Verified:
                break;

            case BillingWebhookVerification.NotConfigured:
                return Failure(BillingWebhookErrors.Unavailable, "This host cannot verify payment provider events.");

            case BillingWebhookVerification.Unreadable:
                logger.LogWarning("A signed payment provider event could not be read as an event.");
                return Failure(BillingWebhookErrors.Unreadable, "The event could not be read.");

            default:
                // Said once, at information: a stranger probing the route is not an operator's problem.
                logger.LogInformation("A payment provider webhook delivery failed signature verification.");
                return Failure(BillingWebhookErrors.SignatureInvalid, "The signature could not be verified.");
        }

        var notice = verified.Notice!;
        var (status, detail) = Classify(notice);
        var recording = await data.RecordAsync(notice, status, detail, CorrelationId(), cancellationToken);

        // The provider's own ids and type, which identify nothing about the creator.
        logger.LogInformation(
            "Payment provider event {ExternalEventId} ({EventType}) {Recording} as {Status}.",
            notice.ExternalEventId,
            notice.EventType,
            recording,
            status);

        return OperationResult<BillingWebhookReceiptServiceModel>.Success(
            new BillingWebhookReceiptServiceModel(recording == BillingEventRecording.Replayed));
    }

    /// <summary>
    /// Which events this host acts on (B-57's table). A pack's checkout and every charge are stored for AP.6.3 and
    /// otherwise left alone; an event from the provider's other mode is stored and ignored.
    /// </summary>
    private (BillingProviderEventStatus Status, string? Detail) Classify(BillingProviderEventNotice notice)
    {
        if (notice.IsLiveMode != (data.Mode == BillingPaymentMode.Live))
        {
            return (BillingProviderEventStatus.Ignored, BillingEventDetails.OtherMode);
        }

        return notice.Kind switch
        {
            BillingEventKind.SubscriptionChanged or BillingEventKind.InvoicePaid or BillingEventKind.InvoicePaymentFailed
                => notice.SubscriptionReference is null
                    ? (BillingProviderEventStatus.Ignored, BillingEventDetails.NoSubscription)
                    : (BillingProviderEventStatus.Received, null),

            // A subscription's checkout links it to its account sooner than its own events might; a pack's grants the
            // credits it paid for (AP.6.3). Either is read again from its session, so one naming none has nothing to do.
            BillingEventKind.CheckoutCompleted => notice.SubscriptionReference is null && notice.ObjectReference is null
                ? (BillingProviderEventStatus.Ignored, BillingEventDetails.NoCheckout)
                : (BillingProviderEventStatus.Received, null),

            // A refund or a dispute takes back the unspent part of the pack its payment bought (B-50, AP.6.3).
            BillingEventKind.ChargeRefunded or BillingEventKind.ChargeDisputed => notice.ObjectReference is null
                ? (BillingProviderEventStatus.Ignored, BillingEventDetails.NoCharge)
                : (BillingProviderEventStatus.Received, null),

            _ => (BillingProviderEventStatus.Ignored, BillingEventDetails.NotHandled),
        };
    }

    public async Task<BillingProviderEventOutcome> ProcessAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var row = await data.FindEventAsync(eventId, cancellationToken);
        if (row is null)
        {
            // A message whose row is gone has nothing left to do; completing it is better than poisoning it.
            logger.LogWarning("Provider event {EventRowId} was queued but is not stored; nothing to process.", eventId);
            return new BillingProviderEventOutcome(BillingProviderEventStatus.Ignored, BillingEventDetails.NotHandled);
        }

        // At-least-once delivery: a message redelivered after its event was settled has already done its work.
        if (row.Status != BillingProviderEventStatus.Received)
        {
            return new BillingProviderEventOutcome(row.Status, BillingEventDetails.AlreadyProcessed);
        }

        if (row.SubscriptionReference is not { } subscriptionId)
        {
            // Not about a subscription, so not about a plan: a pack's checkout, or a payment reversed (AP.6.3). Which
            // of them is the stored kind's answer, never the provider's type string, and a row stored before AP.6.3
            // carries no kind and is left exactly as it was left then.
            var pack = row.Kind switch
            {
                BillingEventKind.CheckoutCompleted =>
                    await packs.GrantAsync(row.ObjectReference, row.IsLiveMode, cancellationToken),

                BillingEventKind.ChargeRefunded =>
                    await packs.ReverseAsync(row.ObjectReference, BillingPackReversal.Refund, row.IsLiveMode, cancellationToken),

                BillingEventKind.ChargeDisputed =>
                    await packs.ReverseAsync(row.ObjectReference, BillingPackReversal.Dispute, row.IsLiveMode, cancellationToken),

                _ => new BillingPackEventOutcome(BillingProviderEventStatus.Ignored, BillingEventDetails.NoSubscription),
            };

            return await CompleteAsync(row, pack.Status, pack.AccountId, pack.Detail, cancellationToken);
        }

        // The record's version before the provider is asked, so a reading that another Worker's newer one overtook
        // while this was in flight is never written over it (RefreshAsync).
        var versionBeforeRead = await data.ReadSubscriptionVersionAsync(subscriptionId, cancellationToken);
        var read = await data.ReadSubscriptionAsync(subscriptionId, cancellationToken);

        switch (read.Outcome)
        {
            case BillingPaymentOutcome.Succeeded:
                break;

            // The provider answered and will answer the same again: an operator's, not a retry's.
            case BillingPaymentOutcome.Refused:
                return await CompleteAsync(row, BillingProviderEventStatus.NeedsAttention, null, BillingEventDetails.ProviderRefused, cancellationToken);
            case BillingPaymentOutcome.Unreadable:
                return await CompleteAsync(row, BillingProviderEventStatus.NeedsAttention, null, BillingEventDetails.ProviderUnreadable, cancellationToken);

            // Not answering, throttled, or this host's credential missing or refused: each may pass, and each is
            // ours to fix rather than the event's, so the message is retried and finally poisoned where it shows.
            default:
                throw new BillingWebhookRetryException($"The payment provider subscription could not be read ({read.Outcome}).");
        }

        var subscription = read.Subscription!;

        if (subscription.IsLiveMode != row.IsLiveMode)
        {
            return await CompleteAsync(row, BillingProviderEventStatus.NeedsAttention, null, BillingEventDetails.OtherMode, cancellationToken);
        }

        // Two facts that must agree before anything is changed: the customer the provider bills is one of ours, and
        // the account the checkout wrote onto the subscription is that customer's. Metadata alone can be edited in
        // the provider's dashboard; the customer alone says nothing about which subscription is meant for whom.
        var customer = await data.FindCustomerAsync(subscription.ExternalCustomerId, cancellationToken);
        if (customer is null || customer.IsLiveMode != subscription.IsLiveMode)
        {
            return await CompleteAsync(row, BillingProviderEventStatus.NeedsAttention, null, BillingEventDetails.CustomerUnknown, cancellationToken);
        }

        if (!string.Equals(subscription.AccountId, customer.AccountId, StringComparison.Ordinal))
        {
            return await CompleteAsync(row, BillingProviderEventStatus.NeedsAttention, null, BillingEventDetails.AccountMismatch, cancellationToken);
        }

        var accountId = customer.AccountId;

        if (subscription.Status == BillingSubscriptionStatus.Unspecified)
        {
            return await CompleteAsync(row, BillingProviderEventStatus.NeedsAttention, accountId, BillingEventDetails.UnknownStatus, cancellationToken);
        }

        var now = clock.UtcNow;
        var subscriptions = await data.RefreshAsync(accountId, subscription, versionBeforeRead, now, cancellationToken)
            ?? throw new BillingWebhookRetryException("Another worker refreshed the same subscription first.");

        var decision = await DecideAsync(accountId, subscriptions, now, cancellationToken);

        if (decision.Desire is null)
        {
            var settled = decision.Detail is BillingEventDetails.Held or BillingEventDetails.Pending or BillingEventDetails.Unchanged
                ? BillingProviderEventStatus.Applied
                : BillingProviderEventStatus.NeedsAttention;

            return await CompleteAsync(row, settled, accountId, decision.Detail, cancellationToken);
        }

        var outcome = await plans.ReconcileAsync(accountId, decision.Desire, AccountPlanSource.Stripe, cancellationToken);

        var (status, detail) = outcome switch
        {
            AccountPlanReconcileOutcome.Changed => (BillingProviderEventStatus.Applied, BillingEventDetails.Changed),
            AccountPlanReconcileOutcome.Unchanged => (BillingProviderEventStatus.Applied, BillingEventDetails.Unchanged),
            AccountPlanReconcileOutcome.Blocked => (BillingProviderEventStatus.NeedsAttention, BillingEventDetails.OpsChangeScheduled),
            AccountPlanReconcileOutcome.NoCatalogue => (BillingProviderEventStatus.NeedsAttention, BillingEventDetails.NoCatalogue),
            AccountPlanReconcileOutcome.UnknownPlan => (BillingProviderEventStatus.NeedsAttention, BillingEventDetails.UnknownPrice),

            // The chain moved under the change. Retried: the next attempt reads it again and decides afresh.
            _ => throw new BillingWebhookRetryException("The account's plan changed while it was being reconciled."),
        };

        // Decided from the records as this Worker found them. Another Worker that refreshed one of them since — a
        // cancellation processed while this renewal was — may have reconciled first, and this reconcile may have just
        // undone it; retried, the next attempt reads the provider again and the last word is the newest reading.
        if (!await data.StillAsDecidedAsync(accountId, subscription.IsLiveMode, subscriptions, cancellationToken))
        {
            throw new BillingWebhookRetryException("Another worker refreshed the account's subscriptions while this one decided.");
        }

        return await CompleteAsync(row, status, accountId, detail, cancellationToken);
    }

    public async Task<(int Requeued, int GivenUp)> RedriveStrandedAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var stranded = await data.ListStrandedAsync(now - BillingWebhookPolicy.StrandedAfter, BillingWebhookPolicy.RedriveBatchSize, cancellationToken);

        var requeued = 0;
        var givenUp = 0;

        foreach (var row in stranded)
        {
            if (row.Redrives >= BillingWebhookPolicy.MaxRedrives)
            {
                await CompleteAsync(row, BillingProviderEventStatus.NeedsAttention, null, BillingEventDetails.ProviderUnavailable, cancellationToken);
                givenUp++;
            }
            else if (await data.RequeueAsync(row.Id, now, CorrelationId(), cancellationToken))
            {
                requeued++;
            }
        }

        return (requeued, givenUp);
    }

    /// <summary>What the account's plan should be from now on, given every subscription it has.</summary>
    /// <returns>A desire to reconcile to, or none with the reason why nothing is to be changed.</returns>
    private async Task<(AccountPlanDesire? Desire, string Detail)> DecideAsync(
        string accountId, IReadOnlyList<BillingSubscription> subscriptions, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var billing = options.Value;
        if (billing.Plans.Count == 0)
        {
            return (null, BillingEventDetails.NoCatalogue);
        }

        var running = subscriptions
            .Where(candidate => candidate.Status is BillingSubscriptionStatus.Active or BillingSubscriptionStatus.Trialing)
            .ToList();

        var chain = await plans.ReadChainAsync(accountId, cancellationToken);
        var inForce = chain.InForce;

        if (running.Count == 0)
        {
            // A renewal the provider is still retrying holds the plan where it is, in either direction.
            if (subscriptions.Any(candidate => candidate.Status == BillingSubscriptionStatus.PastDue))
            {
                return (null, BillingEventDetails.Held);
            }

            // A first payment not taken yet, with nothing before it that has ended: nothing to change yet.
            if (subscriptions.All(candidate => candidate.Status == BillingSubscriptionStatus.Incomplete))
            {
                return (null, BillingEventDetails.Pending);
            }

            // Everything has ended. A subscription takes away only what a subscription gave: a plan the provider put in
            // place goes back to the default now, while an operator's grant or a trial made since is left in force and
            // only the provider's own schedule is withdrawn. A late deletion cannot undo an ops decision.
            if (inForce is { Source: AccountPlanSource.Stripe })
            {
                return (new AccountPlanDesire(AdjustNow: true, NowPlanKey: null, Scheduled: []), BillingEventDetails.Changed);
            }

            return chain.Scheduled.Any(scheduled => scheduled.Source == AccountPlanSource.Stripe)
                ? (new AccountPlanDesire(AdjustNow: false, NowPlanKey: null, Scheduled: []), BillingEventDetails.Changed)
                : (null, BillingEventDetails.Unchanged);
        }

        // A price this host does not sell — set up in the provider's dashboard, or a catalogue key since removed — is
        // never guessed at.
        if (running.Any(candidate => candidate.PlanKey is null || Rank(billing, candidate.PlanKey) < 0))
        {
            return (null, BillingEventDetails.UnknownPrice);
        }

        if (running.Count > 1)
        {
            logger.LogWarning("An account has {Count} running subscriptions; the highest plan among them decides.", running.Count);
        }

        var deciding = running.OrderByDescending(candidate => Rank(billing, candidate.PlanKey!)).First();
        var subscribed = deciding.PlanKey!;

        // B-54 keeps what was paid for, so the wait applies only to a plan the creator paid for through the provider.
        var paidInForce = inForce is { Source: AccountPlanSource.Stripe, PlanKey: not null };
        var steps = new SortedDictionary<DateTimeOffset, string?>();
        string? nowKey = subscribed;

        if (paidInForce && Rank(billing, subscribed) < Rank(billing, inForce!.PlanKey!))
        {
            // A lower plan, while a paid higher one is in force: the higher one runs to the end of the period being
            // paid for. A change this host scheduled earlier keeps its instant; one made elsewhere — link.com, the
            // provider's dashboard — waits for the end of the period it was first seen in.
            var at = chain.Scheduled
                .Where(scheduled => scheduled.Source == AccountPlanSource.Stripe && SameKey(scheduled.PlanKey, subscribed))
                .Select(scheduled => (DateTimeOffset?)scheduled.EffectiveFrom)
                .FirstOrDefault()
                ?? deciding.CurrentPeriodEnd;

            if (at is { } end && end > now)
            {
                nowKey = inForce.PlanKey;
                steps[end] = subscribed;
            }
        }
        else if (Rank(billing, subscribed) > Rank(billing, inForce?.PlanKey ?? billing.DefaultPlan ?? subscribed)
            && !deciding.LatestInvoicePaid)
        {
            // A higher plan whose invoice is still open has not been paid for — an upgrade's proration, invoiced at once
            // (B-57), not settled yet — so the plan in force stays until invoice.paid says it has been.
            nowKey = inForce?.PlanKey;
        }

        // What the subscription's schedule has waiting: a downgrade, or annual to monthly, at the period's end.
        if (deciding.NextPhaseStartsAt is { } phaseStarts && phaseStarts > now)
        {
            if (deciding.NextPlanKey is not { } next || Rank(billing, next) < 0)
            {
                return (null, BillingEventDetails.UnknownPrice);
            }

            steps[phaseStarts] = next;
        }

        // A cancellation is the last word: nothing after it is the subscription's to decide, and on its own instant
        // it wins over a phase that would have started then.
        if (deciding.CancelAt is { } cancelAt && cancelAt > now)
        {
            foreach (var later in steps.Keys.Where(instant => instant > cancelAt).ToList())
            {
                steps.Remove(later);
            }

            steps[cancelAt] = null;
        }

        // Each step a change from the one before it, so a schedule that only changes the cycle writes nothing.
        var scheduled = new List<AccountPlanStep>();
        var previous = nowKey;
        foreach (var (instant, key) in steps)
        {
            if (!SameKey(key, previous))
            {
                scheduled.Add(new AccountPlanStep(key, instant));
                previous = key;
            }
        }

        return (new AccountPlanDesire(AdjustNow: true, nowKey, scheduled), BillingEventDetails.Changed);
    }

    private async Task<BillingProviderEventOutcome> CompleteAsync(
        BillingProviderEvent row,
        BillingProviderEventStatus status,
        string? accountId,
        string detail,
        CancellationToken cancellationToken)
    {
        await data.CompleteAsync(row.Id, status, accountId, detail, clock.UtcNow, cancellationToken);

        if (status == BillingProviderEventStatus.NeedsAttention)
        {
            logger.LogWarning(
                "Payment provider event {ExternalEventId} needs attention: {Detail}.", row.ExternalEventId, detail);
        }

        return new BillingProviderEventOutcome(status, detail);
    }

    /// <summary>A plan's place in the catalogue, cheapest first (the order the landing page renders); -1 for none.</summary>
    private static int Rank(BillingOptions billing, string key)
    {
        for (var index = 0; index < billing.Plans.Count; index++)
        {
            if (SameKey(billing.Plans[index].Key, key))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool SameKey(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static OperationResult<BillingWebhookReceiptServiceModel> Failure(string code, string message) =>
        OperationResult<BillingWebhookReceiptServiceModel>.Failure(
            new OperationError(code, message, new Dictionary<string, string[]>()));

    /// <summary>The request's trace, as <c>AccountPlanBusiness</c> takes it, so the queued work joins the delivery.</summary>
    private static Guid CorrelationId()
    {
        var traceId = Activity.Current?.TraceId;

        return traceId is { } id && id != default ? Guid.ParseExact(id.ToHexString(), "N") : Guid.NewGuid();
    }
}
