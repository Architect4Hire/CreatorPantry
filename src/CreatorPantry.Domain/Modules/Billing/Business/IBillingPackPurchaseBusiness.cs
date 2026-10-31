using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Billing.Data;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Gateways;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Domain.Modules.Billing.Business;

/// <summary>What one pack event did, for the event row the webhook settles (AP.6.3).</summary>
/// <param name="Detail">A word from <see cref="BillingEventDetails"/>, never provider text.</param>
/// <param name="AccountId">The account the event resolved to, when it resolved to one.</param>
internal sealed record BillingPackEventOutcome(
    BillingProviderEventStatus Status, string Detail, string? AccountId = null);

/// <summary>
/// A credit pack bought, refunded or disputed (PRICE-008/010, B-50, AP.6.3): the half of the provider's webhook that
/// moves credits rather than plans.
/// </summary>
internal interface IBillingPackPurchaseBusiness
{
    /// <summary>
    /// Grants the pack a paid checkout bought, once for that checkout, and records the purchase against the grant.
    /// </summary>
    /// <exception cref="BillingWebhookRetryException">
    /// The provider could not be read, or the account's credits moved under the grant: the outbox retries.
    /// </exception>
    Task<BillingPackEventOutcome> GrantAsync(
        string? externalSessionId, bool isLiveMode, CancellationToken cancellationToken);

    /// <summary>
    /// Takes back the unspent part of the grant the reversed payment made, once, and records what was taken. Spent
    /// credits are not recovered, and nothing is taken from the account's period allowance (B-50).
    /// </summary>
    /// <exception cref="BillingWebhookRetryException">
    /// The provider could not be read, the purchase is not recorded yet, or a balance moved: the outbox retries.
    /// </exception>
    Task<BillingPackEventOutcome> ReverseAsync(
        string? externalReference, BillingPackReversal kind, bool isLiveMode, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBillingPackPurchaseBusiness"/>
/// <remarks>
/// <para>
/// <strong>What is granted is the catalogue's, not the provider's.</strong> The session says which price was charged;
/// <c>Billing:Packs</c> says how many credits that pack is. No amount of money crosses into this decision, so a
/// price changed at the provider cannot change what a pack grants, and a pack key edited in the provider's dashboard
/// grants nothing.
/// </para>
/// <para>
/// <strong>The same two facts the plan's path demands</strong> (AP.6.2): the session's customer must be a
/// <see cref="BillingCustomer"/> in the same mode, and the account its metadata names must be that customer's
/// account. Either alone would let a session edited at the provider name somebody else's account.
/// </para>
/// <para>
/// <strong>Granting once is three guards deep.</strong> The event id is unique, so a redelivered delivery stores
/// nothing; the purchase is unique by session, so a second worker records nothing; and the grant is unique by
/// <c>(Purchase, session)</c>, which is what decides a race between two first deliveries. A grant whose purchase row
/// was not written — the process lost between the two saves — is found by the grant's own reference on the retry,
/// which reports it unchanged and records the purchase then, because the event row is settled last of all.
/// </para>
/// <para>
/// <strong>A reversal finds its credits through the payment, never the session</strong>, because that is all a refund
/// or a dispute names: the purchase record is what ties the two together. A payment with no purchase against it and
/// no pack marker on it is something else the provider charged for — a subscription invoice, whose refund takes no
/// credits back (B-54) — and one with the marker but no purchase yet is the two events in flight at once, which is
/// retried rather than guessed at.
/// </para>
/// <para>
/// <strong>Both reversals take the whole unspent balance, once</strong> (B-50, AP.6.3): a partial refund as well as a
/// full one, because credits already spent cannot be returned in part, and a dispute as soon as it is opened. Winning
/// a dispute does not re-grant — <c>charge.dispute.closed</c> is not subscribed (B-57) — so an operator re-grants
/// through the ops route, and this row says what was taken.
/// </para>
/// </remarks>
internal sealed class BillingPackPurchaseBusiness(
    IBillingPackPurchaseDataLayer data,
    IAccountCreditGrantBusiness grants,
    IOptions<BillingOptions> options,
    IClock clock,
    ILogger<BillingPackPurchaseBusiness> logger) : IBillingPackPurchaseBusiness
{
    public async Task<BillingPackEventOutcome> GrantAsync(
        string? externalSessionId, bool isLiveMode, CancellationToken cancellationToken)
    {
        if (externalSessionId is null)
        {
            return new BillingPackEventOutcome(BillingProviderEventStatus.Ignored, BillingEventDetails.NoCheckout);
        }

        // Already recorded: a second event about one checkout — its completion and then its payment's own event — and
        // no reason to ask the provider again.
        if (await data.FindBySessionAsync(externalSessionId, cancellationToken) is { } recorded)
        {
            return new BillingPackEventOutcome(
                BillingProviderEventStatus.Applied, BillingEventDetails.Unchanged, recorded.AccountId);
        }

        var read = await data.ReadCheckoutAsync(externalSessionId, cancellationToken);

        switch (read.Outcome)
        {
            case BillingPaymentOutcome.Succeeded:
                break;

            // The provider answered and will answer the same again: an operator's, not a retry's.
            case BillingPaymentOutcome.Refused:
                return new BillingPackEventOutcome(
                    BillingProviderEventStatus.NeedsAttention, BillingEventDetails.ProviderRefused);
            case BillingPaymentOutcome.Unreadable:
                return new BillingPackEventOutcome(
                    BillingProviderEventStatus.NeedsAttention, BillingEventDetails.ProviderUnreadable);

            default:
                throw new BillingWebhookRetryException($"The pack's checkout could not be read ({read.Outcome}).");
        }

        var checkout = read.Checkout!;

        if (checkout.IsLiveMode != isLiveMode)
        {
            return new BillingPackEventOutcome(
                BillingProviderEventStatus.NeedsAttention, BillingEventDetails.OtherMode);
        }

        if (!checkout.IsPackPurchase)
        {
            return new BillingPackEventOutcome(BillingProviderEventStatus.Ignored, BillingEventDetails.NotAPack);
        }

        // A delayed payment method still settling, or one that failed. Nothing is granted either way, and the
        // payment's own event grants when it succeeds.
        if (!checkout.IsPaid)
        {
            return new BillingPackEventOutcome(BillingProviderEventStatus.Applied, BillingEventDetails.NotPaid);
        }

        var customer = checkout.ExternalCustomerId is { } customerId
            ? await data.FindCustomerAsync(customerId, cancellationToken)
            : null;

        if (customer is null || customer.IsLiveMode != checkout.IsLiveMode)
        {
            return new BillingPackEventOutcome(
                BillingProviderEventStatus.NeedsAttention, BillingEventDetails.CustomerUnknown);
        }

        if (!string.Equals(checkout.AccountId, customer.AccountId, StringComparison.Ordinal))
        {
            return new BillingPackEventOutcome(
                BillingProviderEventStatus.NeedsAttention, BillingEventDetails.AccountMismatch);
        }

        var accountId = customer.AccountId;

        if (options.Value.Packs.Count == 0)
        {
            return new BillingPackEventOutcome(
                BillingProviderEventStatus.NeedsAttention, BillingEventDetails.NoCatalogue, accountId);
        }

        // A price this host does not sell — set up in the provider's dashboard, or a catalogue key since removed — is
        // never guessed at, as a plan's is not (AP.6.2).
        var pack = checkout.PackKey is { } packKey
            ? options.Value.Packs.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, packKey, StringComparison.OrdinalIgnoreCase))
            : null;

        if (pack is null || pack.Credits <= 0)
        {
            return new BillingPackEventOutcome(
                BillingProviderEventStatus.NeedsAttention, BillingEventDetails.UnknownPrice, accountId);
        }

        // No audit actor and no audit row: the verified event and this purchase are the record of who granted these
        // credits and why, which is the caller AP.5.1 wrote that parameter null for.
        var granted = await grants.GrantAsync(
            accountId, pack.Credits, CreditGrantSource.Purchase, externalSessionId, audit: null, cancellationToken);

        var grant = granted.Outcome switch
        {
            AccountCreditGrantOutcome.Granted or AccountCreditGrantOutcome.Unchanged => granted.Grant,

            // One checkout has granted a different figure, or granted to another account: the pack's configured
            // credits changed between two deliveries of one session. Nothing is granted twice over it.
            AccountCreditGrantOutcome.ReferenceUsed => null,

            _ => throw new BillingWebhookRetryException("The account's credits moved while the pack was being granted."),
        };

        if (grant is null)
        {
            logger.LogWarning("A pack's checkout has already granted other credits; nothing was granted.");

            return new BillingPackEventOutcome(
                BillingProviderEventStatus.NeedsAttention, BillingEventDetails.GrantMismatch, accountId);
        }

        var purchase = new BillingPackPurchase
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ExternalSessionId = externalSessionId,
            ExternalPaymentReference = checkout.ExternalPaymentReference,
            IsLiveMode = checkout.IsLiveMode,
            PackKey = pack.Key,
            CreditsGranted = grant.CreditsGranted,
            GrantId = grant.Id,
            PurchasedAt = clock.UtcNow,
        };

        if (!await data.RecordPurchaseAsync(purchase, cancellationToken)
            && await data.FindBySessionAsync(externalSessionId, cancellationToken) is null)
        {
            // Lost the session or payment index to a writer whose row is not there either: not a race after all, and
            // the credits are granted with no record of the payment, so it is tried again.
            throw new BillingWebhookRetryException("The pack's purchase could not be recorded.");
        }

        return new BillingPackEventOutcome(
            BillingProviderEventStatus.Applied,
            granted.Outcome == AccountCreditGrantOutcome.Granted
                ? BillingEventDetails.Granted
                : BillingEventDetails.Unchanged,
            accountId);
    }

    public async Task<BillingPackEventOutcome> ReverseAsync(
        string? externalReference, BillingPackReversal kind, bool isLiveMode, CancellationToken cancellationToken)
    {
        if (kind == BillingPackReversal.None)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A reversal is a refund or a dispute.");
        }

        if (externalReference is null)
        {
            return new BillingPackEventOutcome(BillingProviderEventStatus.Ignored, BillingEventDetails.NoCharge);
        }

        var read = await data.ReadReversalAsync(externalReference, kind, cancellationToken);

        switch (read.Outcome)
        {
            case BillingPaymentOutcome.Succeeded:
                break;

            case BillingPaymentOutcome.Refused:
                return new BillingPackEventOutcome(
                    BillingProviderEventStatus.NeedsAttention, BillingEventDetails.ProviderRefused);
            case BillingPaymentOutcome.Unreadable:
                return new BillingPackEventOutcome(
                    BillingProviderEventStatus.NeedsAttention, BillingEventDetails.ProviderUnreadable);

            default:
                throw new BillingWebhookRetryException($"The reversal could not be read ({read.Outcome}).");
        }

        var reversal = read.Reversal!;

        if (reversal.IsLiveMode != isLiveMode)
        {
            return new BillingPackEventOutcome(
                BillingProviderEventStatus.NeedsAttention, BillingEventDetails.OtherMode);
        }

        // Every pack is paid for through a checkout, so a reversal naming no payment reverses nothing of this host's.
        if (reversal.ExternalPaymentReference is not { } paymentReference)
        {
            return new BillingPackEventOutcome(BillingProviderEventStatus.Ignored, BillingEventDetails.NotAPack);
        }

        // Read again and the provider does not say money went back. Nothing is taken on an event alone.
        if (!reversal.IsReversed)
        {
            return new BillingPackEventOutcome(BillingProviderEventStatus.Applied, BillingEventDetails.NotReversed);
        }

        var purchase = await data.FindByPaymentForUpdateAsync(paymentReference, cancellationToken);

        if (purchase is null)
        {
            // A payment that bought no pack: a subscription's invoice, most often, whose refund takes no credits back.
            if (!reversal.IsPackPurchase)
            {
                return new BillingPackEventOutcome(BillingProviderEventStatus.Ignored, BillingEventDetails.NotAPack);
            }

            // A pack's payment whose own checkout is not recorded yet: both events in flight at once. Retried, and
            // finally left for an operator by the Worker's sweep rather than quietly doing nothing.
            throw new BillingWebhookRetryException("The reversed pack's purchase has not been recorded yet.");
        }

        // The two facts the plan's path demands, in the one shape they take here: the payment's own metadata, where it
        // has any, must name the account the purchase is for. Metadata can be edited at the provider, and a claw-back
        // against the wrong account is not a thing to guess at.
        if (reversal.AccountId is { } named && !string.Equals(named, purchase.AccountId, StringComparison.Ordinal))
        {
            return new BillingPackEventOutcome(
                BillingProviderEventStatus.NeedsAttention, BillingEventDetails.AccountMismatch, purchase.AccountId);
        }

        // Once: a refund and then a dispute on one payment, or either delivered twice, has nothing left to take.
        if (purchase.ReversedAt is not null)
        {
            return new BillingPackEventOutcome(
                BillingProviderEventStatus.Applied, BillingEventDetails.Unchanged, purchase.AccountId);
        }

        var clawedBack = await grants.ClawBackAsync(purchase.AccountId, purchase.GrantId, audit: null, cancellationToken);

        var taken = clawedBack.Outcome switch
        {
            // Unchanged as well as ClawedBack, because the grant may have been taken back already — by an operator, or
            // by a reversal whose own record was lost — and this row then records the figure that grant holds.
            AccountCreditGrantOutcome.ClawedBack or AccountCreditGrantOutcome.Unchanged =>
                clawedBack.Grant!.CreditsClawedBack,

            AccountCreditGrantOutcome.NotFound => (decimal?)null,

            _ => throw new BillingWebhookRetryException(
                "The account's credits moved while the pack was being clawed back."),
        };

        if (taken is not { } credits)
        {
            return new BillingPackEventOutcome(
                BillingProviderEventStatus.NeedsAttention, BillingEventDetails.GrantMissing, purchase.AccountId);
        }

        purchase.Reversal = kind;
        purchase.ExternalReversalReference = externalReference;
        purchase.CreditsClawedBack = credits;
        purchase.ReversedAt = clock.UtcNow;

        if (!await data.RecordReversalAsync(purchase, cancellationToken))
        {
            throw new BillingWebhookRetryException("Another worker recorded a reversal of the same payment first.");
        }

        return new BillingPackEventOutcome(
            BillingProviderEventStatus.Applied, BillingEventDetails.ClawedBack, purchase.AccountId);
    }
}
