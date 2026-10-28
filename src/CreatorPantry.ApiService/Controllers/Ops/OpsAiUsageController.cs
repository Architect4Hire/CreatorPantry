using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Billing.Facade;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.ApiService.Controllers.Ops;

/// <summary>
/// Platform administration of any account's AI allowance (USAGE-009) and the plan it is on (PRICE-007): read
/// them, set or clear its quota, put it on a plan, suspend or restore its access, and list the
/// highest-consuming accounts for a period or the accounts on a plan.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One scope covers all of it</strong> (<c>OpsScopes.AiUsageAdmin</c>). A credential that can set an
/// account's quota can also set its plan, which is deliberate — a plan <em>is</em> the allowance since AP.4.2,
/// and splitting the two would issue a key that could raise an allowance but not the plan granting it. A
/// read-only automation still needs its own key, which is what naming the capability buys.
/// </para>
/// <para>
/// <strong>An ops route, and only an ops route</strong> (baseline B-14). Callable only with a hashed,
/// rotatable ops API key carrying the AI-usage administration scope; the gateway refuses to proxy
/// <c>/api/*/ops/**</c> from a browser, so these are reached directly and never from a creator's session.
/// A Workspace Owner is not a platform administrator, and holding <c>PlatformAdmin</c> grants no ops access
/// either — the policy names a scheme no gateway-minted token can satisfy.
/// </para>
/// <para>
/// <strong>Not published in OpenAPI</strong>, like the internal session routes: the reviewed v1 document is the
/// browser-facing contract, and publishing an API-key security scheme in it would advertise a credential the
/// SPA must never hold.
/// </para>
/// <para>
/// <strong>Numbers, accounts and workspace identifiers.</strong> Nothing returned here carries a recipe title,
/// a prompt, a proposal, or a workspace name — an administrator is not a member of the workspaces they can
/// count spend in.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/ops/ai-usage")]
[Authorize(Policy = AuthorizationPolicies.Ops)]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class OpsAiUsageController(
    IAiUsageAdministrationFacade facade,
    IAccountPlanFacade plans,
    ILogger<OpsAiUsageController> logger) : ControllerBase
{
    /// <summary>One account's running period, the terms in force, and what its spend went on.</summary>
    /// <remarks>
    /// The period and the terms are both returned because they can legitimately disagree: a period freezes the
    /// allowance it opened with, so a change made today binds at the next roll. Suspension is the exception —
    /// it is read live and takes effect on the account's next request.
    /// </remarks>
    [HttpGet("accounts/{accountId}")]
    public async Task<IActionResult> GetAccount(string accountId, CancellationToken cancellationToken)
    {
        RecordRead("account", accountId);

        return Respond(await facade.GetAccountAsync(accountId, cancellationToken));
    }

    /// <summary>The highest-consuming accounts between <paramref name="from"/> and <paramref name="to"/>.</summary>
    /// <param name="from">Inclusive, ISO 8601 UTC.</param>
    /// <param name="to">Exclusive, ISO 8601 UTC.</param>
    /// <param name="limit">Clamped to <see cref="AiUsageAdministrationPolicy.TopConsumersMaxLimit"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpGet("top-consumers")]
    public async Task<IActionResult> GetTopConsumers(
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromQuery] int limit = AiUsageAdministrationPolicy.TopConsumersDefaultLimit,
        CancellationToken cancellationToken = default)
    {
        RecordRead("top-consumers", $"{from:O}/{to:O}");

        return Respond(await facade.GetTopConsumersAsync(from, to, limit, cancellationToken));
    }

    /// <summary>Puts explicit quota terms in force for the account.</summary>
    /// <remarks>
    /// Does not touch suspension: raising an allowance is not a way to restore an account somebody else
    /// switched off. A request whose terms already match writes nothing and reports <c>unchanged</c>, which is
    /// what makes a retry safe without an idempotency key.
    /// </remarks>
    [HttpPut("accounts/{accountId}/quota")]
    public async Task<IActionResult> SetQuota(
        string accountId, SetAccountAiQuotaViewModel model, CancellationToken cancellationToken) =>
        Respond(await facade.SetQuotaAsync(accountId, model, User.OpsActor(), cancellationToken));

    /// <summary>Returns the account to the configured platform default in every term.</summary>
    /// <remarks>
    /// An action resource rather than <c>DELETE</c>, because the change must carry a reason and the audit row
    /// requires one — a delete with a body is a worse contract than a command that says what it is.
    /// </remarks>
    [HttpPost("accounts/{accountId}/quota/clear")]
    public async Task<IActionResult> ClearQuota(
        string accountId, AccountAiQuotaReasonViewModel model, CancellationToken cancellationToken) =>
        Respond(await facade.ClearQuotaAsync(accountId, model, User.OpsActor(), cancellationToken));

    /// <summary>Switches the account's AI access off. Takes effect on its next request.</summary>
    [HttpPost("accounts/{accountId}/ai-access/suspend")]
    public async Task<IActionResult> Suspend(
        string accountId, AccountAiQuotaReasonViewModel model, CancellationToken cancellationToken) =>
        Respond(await facade.SetSuspensionAsync(
            accountId, suspended: true, model, User.OpsActor(), cancellationToken));

    /// <summary>Switches the account's AI access back on.</summary>
    [HttpPost("accounts/{accountId}/ai-access/restore")]
    public async Task<IActionResult> Restore(
        string accountId, AccountAiQuotaReasonViewModel model, CancellationToken cancellationToken) =>
        Respond(await facade.SetSuspensionAsync(
            accountId, suspended: false, model, User.OpsActor(), cancellationToken));

    /// <summary>
    /// The platform's estimated image spend for a UTC day against its daily ceiling, and whether the breaker
    /// has tripped or been lifted (PRICE-005, AP.2.7).
    /// </summary>
    /// <param name="day">A UTC calendar day, <c>yyyy-MM-dd</c>. Omitted means today.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Figures in US dollars at provider list price, summed over every account — which is exactly why this is
    /// an ops route and nothing a creator can reach shows any of it.
    /// </remarks>
    [HttpGet("platform/image-spend")]
    public async Task<IActionResult> GetPlatformImageSpend(
        [FromQuery] DateOnly? day, CancellationToken cancellationToken)
    {
        RecordRead("platform-image-spend", day?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "today");

        return Respond(await facade.GetPlatformImageSpendAsync(day, cancellationToken));
    }

    /// <summary>
    /// Lifts the image spend breaker for the rest of the current UTC day. Audited with the reason given.
    /// </summary>
    /// <remarks>
    /// Today only: the next UTC day starts armed again. A day already lifted reports <c>unchanged</c> and writes
    /// no second audit row, which is what makes a retry safe without an idempotency key. Refused with a 422 on
    /// a host that has no ceiling configured.
    /// </remarks>
    [HttpPost("platform/image-spend/lift")]
    public async Task<IActionResult> LiftPlatformImageSpend(
        PlatformImageSpendLiftViewModel model, CancellationToken cancellationToken) =>
        Respond(await facade.LiftPlatformImageSpendAsync(model, User.OpsActor(), cancellationToken));

    // ---- Which plan an account is on (PRICE-007, AP.4.4) -------------------------------------------------

    /// <summary>The plan the account is on now, its terms, and when a scheduled change takes over.</summary>
    /// <remarks>
    /// An account with no plan row of its own reads as the configured default and says so (<c>isDefault</c>),
    /// which is not the same as having been put on that plan deliberately — <c>source</c> and
    /// <c>effectiveFrom</c> are null when no row is in force. A host with no catalogue answers
    /// `404 ops.account_plan.catalogue.not_found`: there is no plan to report, and inventing one would have an
    /// operator act on a plan nobody is on.
    ///
    /// **The account identifier is not verified.** Billing has no table of accounts — Identity owns those — so
    /// any string reads as the configured default rather than 404, and a `PUT` will happily start a plan chain
    /// for one. An operator pasting a wrong id gets a plausible answer, so the id is worth checking against
    /// `GET .../accounts/{accountId}`, which does know whether an account has ever used anything.
    /// </remarks>
    [HttpGet("accounts/{accountId}/plan")]
    public async Task<IActionResult> GetPlan(string accountId, CancellationToken cancellationToken)
    {
        RecordRead("account-plan", accountId);

        var plan = await plans.ResolveAsync(accountId, cancellationToken);

        return Respond(plan is null
            ? OperationResult<AccountPlanServiceModel>.Failure(new OperationError(
                AccountPlanAdministrationErrors.NoCatalogue,
                "No plan catalogue is configured on this host.",
                new Dictionary<string, string[]>()))
            : OperationResult<AccountPlanServiceModel>.Success(plan));
    }

    /// <summary>Puts the account on a plan, or schedules the change for a later instant.</summary>
    /// <remarks>
    /// <para>
    /// <c>planKey</c> omitted or null means "the configured default", which is what a stored row with no key
    /// means too — so an account put there tracks a later change of <c>Billing:DefaultPlan</c> rather than being
    /// pinned to whatever it happens to be today.
    /// </para>
    /// <para>
    /// A future <c>effectiveFrom</c> schedules it, which is how a downgrade at period end is recorded (B-54);
    /// the answer then says <c>scheduled</c> and carries the plan still in force, not the one coming. A
    /// <c>reason</c> is required and is stored in the platform audit row with the change, in one transaction.
    /// </para>
    /// <para>
    /// **Safe to retry, and that is why it is a `PUT`.** The same command sent twice writes once: a request
    /// naming the plan the account is already on, from the instant it is already on it, answers
    /// <c>outcome: Unchanged</c> and writes neither a chain row nor an audit entry. Without that a chain is
    /// append-only, so an operator's double submit would record a change that did not happen.
    /// </para>
    /// <para>
    /// `422 ops.account_plan.unprocessable` for a key the catalogue does not have, an account id over 450
    /// characters, or a start that has passed or does not follow the account's latest row;
    /// `404 ops.account_plan.catalogue.not_found` on a host with no catalogue;
    /// `409 ops.account_plan.conflict` when another writer changed the plan first (<c>retry: reread</c>);
    /// `409 ops.account_plan.change_scheduled.conflict` for a change asked for now while a later one is already
    /// scheduled — nothing is written, and replacing a scheduled change is AP.6.2's.
    /// </para>
    /// </remarks>
    [HttpPut("accounts/{accountId}/plan")]
    public async Task<IActionResult> SetPlan(
        string accountId, SetAccountPlanViewModel model, CancellationToken cancellationToken) =>
        Respond(await plans.ChangeAsync(accountId, model, User.OpsActor(), cancellationToken));

    /// <summary>The accounts whose plan in force is <paramref name="planKey"/>, a page at a time.</summary>
    /// <param name="planKey">A catalogue key. One the catalogue does not have answers 422.</param>
    /// <param name="cursor">The <c>nextCursor</c> from the previous page; omitted for the first.</param>
    /// <param name="limit">Clamped to <see cref="AccountPlanAdministrationPolicy.ListMaxLimit"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// <para>
    /// <strong>Stored rows only.</strong> An account that has never been moved is on the configured default by
    /// absence and has no row to list; Billing has no table of accounts, Identity does. So this answers "whose
    /// stored plan is this", not "who is on this plan" — on a host whose default plan most accounts have never
    /// been moved off, the second question has no answer here.
    /// </para>
    /// <para>
    /// Asking for the default plan's key does include accounts whose row says "the default", and accounts whose
    /// row names a plan the catalogue no longer has: both resolve to the default, so leaving either out would
    /// make the answer quietly incomplete just after a plan was withdrawn.
    /// </para>
    /// <para>
    /// A scheduled change is not listed under the plan it will move the account to — the question is who is on
    /// this plan, not who will be. Ordered by account id, which is also the cursor, so a page stays stable while
    /// plans move underneath it. Identifiers and instants only.
    /// </para>
    /// </remarks>
    [HttpGet("accounts/by-plan")]
    public async Task<IActionResult> GetAccountsByPlan(
        [FromQuery] string planKey,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = AccountPlanAdministrationPolicy.ListDefaultLimit,
        CancellationToken cancellationToken = default)
    {
        RecordRead("accounts-by-plan", planKey);

        return Respond(await plans.ListByPlanAsync(planKey, cursor, limit, cancellationToken));
    }

    /// <summary>
    /// Leaves a trace of what a machine credential looked at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A log line rather than a <c>PlatformAuditLog</c> row</strong>, because these are GETs and a GET
    /// that writes to the database is the surprise this feature spends its read path avoiding. The writes are
    /// audited durably; the reads are traceable.
    /// </para>
    /// <para>
    /// It records the ops client and which account or window was asked about — an identifier and a date range,
    /// which is all these routes deal in anyway. Never a key, never a figure.
    /// </para>
    /// </remarks>
    private void RecordRead(string what, string subject)
    {
        var actor = User.OpsActor();

        logger.LogInformation(
            "Ops client {OpsClientName} ({OpsClientId}) read {OpsReadKind} {OpsReadSubject}.",
            actor.Name, actor.Id, what, subject);
    }

    private IActionResult Respond<T>(OperationResult<T> result) =>
        result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
}
