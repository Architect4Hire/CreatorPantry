using Asp.Versioning;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.FileInspection;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Billing.Facade;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>
/// The payment provider's webhook (PRICE-010, AP.6.2): Stripe tells CreatorPantry what happened to a subscription,
/// and this stores it to act on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Anonymous, and authenticated by the body's signature alone.</strong> No browser session and no internal
/// token reach it — the gateway attaches none on this route — and none would be trusted if they did. The provider's
/// HMAC over the raw bytes is the credential, checked before a byte of them is parsed; an unsigned, re-signed or stale
/// delivery is refused with nothing stored.
/// </para>
/// <para>
/// <strong>Answers at once.</strong> A verified event is stored and its work queued in one save, then acknowledged;
/// the subscription is read again and the plan changed from the outbox, so a slow provider never times this route
/// out and a redelivery is answered from the stored event id with nothing done twice.
/// </para>
/// <para>
/// The body is read raw and bounded rather than bound: model binding would parse it, and parsing it before the
/// signature holds is exactly what the route must not do.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/webhooks/stripe")]
public sealed class BillingWebhooksController(IBillingWebhookFacade webhookFacade) : ControllerBase
{
    /// <summary>The header Stripe signs each delivery in.</summary>
    internal const string SignatureHeader = "Stripe-Signature";

    /// <summary>
    /// Takes one event delivery from Stripe. 200 for a verified event, new or already stored; 400 for a delivery whose
    /// signature does not verify; 503 on a host given no signing secret, which Stripe retries.
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    [ProviderWebhook(SignatureHeader)]
    [RequestSizeLimit(BillingWebhookPolicy.MaxBodyBytes)]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        if (!await BoundedCopy.TryCopyAsync(Request.Body, body, BillingWebhookPolicy.MaxBodyBytes, cancellationToken))
        {
            // The action's limit stops a body that says how long it is; this one stops a chunked body that did not.
            // Through ProblemFor like every other refusal, so it carries a stable code and its retry advice.
            return this.ProblemFor(new OperationError(
                BillingWebhookErrors.PayloadTooLarge,
                "The event is larger than this route accepts.",
                new Dictionary<string, string[]>()));
        }

        var signature = Request.Headers[SignatureHeader].ToString();
        var result = await webhookFacade.ReceiveAsync(
            string.IsNullOrEmpty(signature) ? null : signature,
            body.GetBuffer().AsMemory(0, (int)body.Length),
            cancellationToken);

        // Nothing in the answer: the provider reads only the status, and a body would only be something to leak.
        return result.Succeeded ? Ok() : this.ProblemFor(result.Error!);
    }
}
