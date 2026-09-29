using Asp.Versioning;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>The signed-in user's own cross-workspace view. Requires a user session (default policy).</summary>
/// <remarks>
/// <strong>Nothing here is nested under <c>/workspaces/{workspaceSlug}</c>, and that is the point.</strong>
/// These routes answer questions that span workspaces — which ones the caller belongs to, what their AI
/// allowance has gone on across all of them — and a workspace-scoped route could only narrow that to one or
/// answer about workspaces it does not name.
/// </remarks>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/me")]
public sealed class MeController(
    IWorkspaceFacade workspaceFacade, IAiUsageReadFacade aiUsageFacade) : ControllerBase
{
    /// <summary>Every workspace membership the signed-in user holds, active or not.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<MyWorkspaceMembershipServiceModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        Ok(await workspaceFacade.GetMyMembershipsAsync(AccountId, cancellationToken));

    /// <summary>
    /// The signed-in account's AI allowance for the running period, and what it has gone on (USAGE-008).
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// <para>
    /// The totals are the period's own unit; both breakdowns are in it too and both sum to <c>consumed</c>.
    /// Request counts travel beside the amounts because work done before allowances existed was never charged
    /// against one, and would otherwise read as no usage at all.
    /// </para>
    /// <para>
    /// A workspace the account no longer holds active membership in keeps its totals and is returned with a
    /// null name. The spend is the creator's own history; the name is not theirs to have any more.
    /// </para>
    /// <para>
    /// Takes no parameters, and there is deliberately nowhere to put an account id — not a route segment, a
    /// body, a query value or a header. The account comes from the validated session.
    /// </para>
    /// </remarks>
    [HttpGet("ai-usage")]
    [ProducesResponseType<AccountAiUsageServiceModel>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAiUsage(CancellationToken cancellationToken) =>
        Ok(await aiUsageFacade.GetCurrentAsync(AccountId, cancellationToken));

    /// <summary>The signed-in account's closed AI allowance periods, newest first.</summary>
    /// <param name="limit">
    /// How many periods to return. Clamped to <see cref="AiUsagePolicy.HistoryMaxPeriods"/>; a value below one
    /// is clamped up rather than refused, because a paging control that briefly sends zero is not a caller
    /// error worth a 400.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// The running period is not included: <c>GET /api/v1/me/ai-usage</c> is what answers about it, and
    /// returning it here as well would show the same period twice with two totals a moment apart.
    /// </remarks>
    [HttpGet("ai-usage/history")]
    [ProducesResponseType<IReadOnlyList<AccountAiUsagePeriodServiceModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAiUsageHistory(
        [FromQuery] int limit = AiUsagePolicy.HistoryDefaultPeriods,
        CancellationToken cancellationToken = default) =>
        Ok(await aiUsageFacade.GetHistoryAsync(AccountId, limit, cancellationToken));

    /// <summary>
    /// The signed-in account, from the validated token.
    /// </summary>
    /// <remarks>
    /// The one place these routes learn who is asking. USAGE-008 forbids accepting an account id from a route,
    /// body, query or header, and this is what makes that structural: no action here binds a parameter that
    /// could carry one, so there is nothing to ignore and nothing to validate.
    /// </remarks>
    private string AccountId => User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
}
