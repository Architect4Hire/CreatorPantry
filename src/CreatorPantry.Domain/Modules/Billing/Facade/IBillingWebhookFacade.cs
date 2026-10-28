using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Billing.Business;
using CreatorPantry.Domain.Modules.Billing.Managers;

namespace CreatorPantry.Domain.Modules.Billing.Facade;

/// <summary>
/// The application boundary for the payment provider's webhook (PRICE-010, AP.6.2): the API's public route takes a
/// delivery through it, and the Worker's outbox handler acts on what it stored.
/// </summary>
/// <remarks>
/// <strong>No caller identity, and none needed.</strong> The route takes no browser session and no internal token:
/// the provider's signature over the raw body is the only credential, checked before anything in the body is read.
/// Nothing here takes an account or a workspace from the caller; an event's account is the one its subscription's
/// customer maps to (<c>BillingCustomer</c>), resolved in processing.
/// </remarks>
public interface IBillingWebhookFacade
{
    /// <param name="signatureHeader">The delivery's signature header as received, or null.</param>
    /// <param name="body">The raw body, at most <see cref="BillingWebhookPolicy.MaxBodyBytes"/>, exactly as received.</param>
    Task<OperationResult<BillingWebhookReceiptServiceModel>> ReceiveAsync(
        string? signatureHeader, ReadOnlyMemory<byte> body, CancellationToken cancellationToken);

    /// <summary>Acts on one stored event. For the outbox handler; safe to call again for the same event.</summary>
    Task<BillingProviderEventOutcome> ProcessAsync(Guid eventId, CancellationToken cancellationToken);

    /// <summary>
    /// Queues again the events left unprocessed after their messages gave up, for the Worker's sweep; gives up on one
    /// after a day of trying, as <see cref="BillingProviderEventStatus.NeedsAttention"/>.
    /// </summary>
    Task<(int Requeued, int GivenUp)> RedriveStrandedAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBillingWebhookFacade"/>
internal sealed class BillingWebhookFacade(IBillingWebhookBusiness business) : IBillingWebhookFacade
{
    public Task<OperationResult<BillingWebhookReceiptServiceModel>> ReceiveAsync(
        string? signatureHeader, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        // The route bounds the body before it gets here; this holds every other caller to the same bound.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(body.Length, BillingWebhookPolicy.MaxBodyBytes, nameof(body));

        return business.ReceiveAsync(signatureHeader, body, cancellationToken);
    }

    public Task<BillingProviderEventOutcome> ProcessAsync(Guid eventId, CancellationToken cancellationToken) =>
        business.ProcessAsync(eventId, cancellationToken);

    public Task<(int Requeued, int GivenUp)> RedriveStrandedAsync(CancellationToken cancellationToken) =>
        business.RedriveStrandedAsync(cancellationToken);
}
