using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.ApiService.Controllers.Ops;

/// <summary>
/// Platform administration of any account's AI allowance (USAGE-009): read it, set or clear its quota,
/// suspend or restore its access, and list the highest-consuming accounts for a period.
/// </summary>
/// <remarks>
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
    IAiUsageAdministrationFacade facade, ILogger<OpsAiUsageController> logger) : ControllerBase
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
