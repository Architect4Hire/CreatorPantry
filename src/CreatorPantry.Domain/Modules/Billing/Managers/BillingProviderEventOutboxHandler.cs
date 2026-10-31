using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Modules.Billing.Data;
using CreatorPantry.Domain.Modules.Billing.Facade;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Modules.Billing.Managers;

/// <summary>
/// Acts on one stored payment provider event, after the delivery that stored it has been answered (AP.6.2: 2xx at
/// once, the work from the outbox).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A fresh scope per message</strong>, as <c>BrandStyleGuideActivatedOutboxHandler</c> takes one: the plan
/// writes save on their own context, and a lost race there forgets everything tracked on it — which must not include
/// the dispatcher's own lease on this message.
/// </para>
/// <para>
/// <strong>Failure semantics.</strong> A payload that does not parse, a provider that is not answering and a plan
/// chain that moved under the change all throw, so the outbox retries with backoff and finally poisons the message
/// where it stays visible. Every attempt starts from the provider's subscription as it is then, so a retry can never
/// apply a stale answer. A poisoned message leaves its event <c>Received</c>, and the Worker's sweep
/// (<c>RedriveStrandedAsync</c>) queues it again a quarter of an hour on, for a day, before it asks for an operator. No
/// workspace is involved: a plan is the account's (B-47), and the account is resolved from the provider's customer.
/// </para>
/// </remarks>
internal sealed class BillingProviderEventOutboxHandler(IServiceScopeFactory scopeFactory) : IOutboxMessageHandler
{
    public async Task HandleAsync(OutboxMessageEnvelope message, CancellationToken cancellationToken)
    {
        var payload = BillingProviderEventMessage.TryParse(message.PayloadJson)
            ?? throw new InvalidOperationException("The billing provider event payload is malformed.");

        await using var scope = scopeFactory.CreateAsyncScope();

        await scope.ServiceProvider
            .GetRequiredService<IBillingWebhookFacade>()
            .ProcessAsync(payload.EventId, cancellationToken);
    }
}
