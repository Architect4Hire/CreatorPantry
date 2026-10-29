using Asp.Versioning;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Billing.Facade;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>
/// The signed-in account's billing (AP.6.5, B-57): what its billing page shows, and the Checkout, portal and
/// plan-change commands behind its buttons.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The account, never a workspace.</strong> A plan and a pack are the person's across every workspace they
/// work in (B-47), so these routes sit under <c>/me</c> and learn who is asking from the session alone.
/// </para>
/// <para>
/// <strong>Nothing here is a purchase.</strong> A command answers with the payment provider's page to go to, or says a
/// change is scheduled; the plan moves and the credits arrive when the provider's verified webhook does (B-60). The
/// page you return to reads <c>GET .../checkout/{sessionId}</c> until it has.
/// </para>
/// <para>
/// Every POST and the DELETE take an <c>Idempotency-Key</c>; a retry with the same key answers what the first did. A
/// host that takes no payments answers <c>503 billing.payments.unavailable</c> with <c>retry: later</c>, as it does when
/// the provider cannot be reached; no provider body reaches a response.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/me/billing")]
public sealed class MeBillingController(IBillingAccountFacade billing) : ControllerBase
{
    /// <summary>The account's plan, its subscription, its pack balance, and what it may buy now.</summary>
    [HttpGet]
    [ProducesResponseType<BillingAccountServiceModel>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        Ok(await billing.GetAsync(AccountId, cancellationToken));

    /// <summary>Starts hosted Checkout for a first subscription (B-57).</summary>
    /// <remarks>
    /// `planKey` is a paid plan this catalogue offers and `cycle` is `Monthly` or `Annual`; anything else answers
    /// `422 billing.plan.unprocessable`. An account that already has a live subscription answers
    /// `409 billing.checkout.subscribed.conflict`, `retry: reread` — a second Checkout would make a second subscription,
    /// so it changes plan instead. `returnOrigin` is the page's own origin, used only when this host lists it.
    /// </remarks>
    [HttpPost("checkout/subscription")]
    [ProducesResponseType<BillingRedirectServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> StartPlanCheckout(
        StartPlanCheckoutViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken) =>
        this.IdempotentResult(
            await billing.StartPlanCheckoutAsync(AccountId, model, idempotencyKey, cancellationToken), Ok);

    /// <summary>Starts hosted Checkout for a credit pack (B-57).</summary>
    /// <remarks>
    /// An account whose free trial has ended while it is still on the trial answers
    /// `403 billing.pack.plan_required.forbidden`: no AI work is admitted then, pack credits included (B-61), so a plan
    /// comes first. An unknown pack answers `422 billing.pack.unprocessable`.
    /// </remarks>
    [HttpPost("checkout/pack")]
    [ProducesResponseType<BillingRedirectServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> StartPackCheckout(
        StartPackCheckoutViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken) =>
        this.IdempotentResult(
            await billing.StartPackCheckoutAsync(AccountId, model, idempotencyKey, cancellationToken), Ok);

    /// <summary>Changes the plan of an account already paying: up at once, anything else at period end.</summary>
    /// <remarks>
    /// A plan with a higher monthly price answers with the provider's page to confirm and pay the prorated difference
    /// (B-56). Anything else — a lower plan, or the other cycle — is scheduled for the end of the paid period (B-54) and
    /// answers `scheduled: true` with no page. No live subscription answers
    /// `409 billing.plan_change.no_subscription.conflict`; the plan and cycle already in force answer
    /// `409 billing.plan_change.unchanged.conflict`.
    /// </remarks>
    [HttpPost("plan-change")]
    [ProducesResponseType<BillingRedirectServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ChangePlan(
        ChangePlanViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken) =>
        this.IdempotentResult(await billing.ChangePlanAsync(AccountId, model, idempotencyKey, cancellationToken), Ok);

    /// <summary>Withdraws a plan change scheduled for the end of the period.</summary>
    /// <remarks>Nothing scheduled answers `404 billing.plan_change.not_found`.</remarks>
    [HttpDelete("plan-change")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> WithdrawPlanChange(
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken) =>
        this.IdempotentResult(
            await billing.WithdrawPlanChangeAsync(AccountId, idempotencyKey, cancellationToken), _ => NoContent());

    /// <summary>Opens the provider's portal: payment methods, invoices, cancelling at period end.</summary>
    /// <remarks>An account that has never paid answers `404 billing.portal.not_found`.</remarks>
    [HttpPost("portal")]
    [ProducesResponseType<BillingRedirectServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> OpenPortal(OpenBillingPortalViewModel model, CancellationToken cancellationToken)
    {
        var result = await billing.OpenPortalAsync(AccountId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Whether what a Checkout bought has landed, for the page the provider sends the creator back to.</summary>
    /// <remarks>
    /// `kind` is `Plan` or `Pack`, as the return URL says. `Pending` until the provider's webhook has put the plan in
    /// force or granted the pack — a browser arriving back is not a payment. Another account's session reads as pending.
    /// </remarks>
    [HttpGet("checkout/{sessionId}")]
    [ProducesResponseType<BillingCheckoutStatusServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetCheckout(
        string sessionId, [FromQuery] BillingCheckoutKind kind, CancellationToken cancellationToken)
    {
        var result = await billing.GetCheckoutAsync(AccountId, kind, sessionId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>The signed-in account, from the validated token — never a route, body, query or header (USAGE-008).</summary>
    private string AccountId => User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
}
