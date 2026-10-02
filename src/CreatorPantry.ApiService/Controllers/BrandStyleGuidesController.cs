using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/brand-style-guides")]
public sealed class BrandStyleGuidesController(IBrandStyleGuideFacade guides) : ControllerBase
{
    /// <summary>Creates a brand style guide and its first version.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="model">
    /// The guide's name, an optional short questionnaire, optional structured sections and optional cited
    /// source document versions. Carries no workspace.
    /// </param>
    /// <param name="idempotencyKey">
    /// Required. Replaying a request with the same key returns the guide the first one created, with
    /// `Idempotency-Replayed: true`; the same key with a different body is refused.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Editor or above. Everything but `displayName` is optional, and a blank answer creates nothing: a guide
    /// is only what its creator wrote. Questionnaire answers and `sections` are two ways to write the same
    /// section rows, so naming one section in both is a `400`. Text is stored exactly as typed.
    ///
    /// `sourceDocuments` cites exact versions by document id and version number. A pointer that does not
    /// resolve — never issued, another workspace's, removed, or a version the document does not have — answers
    /// `422 brand.guide.source.unprocessable`, identically for every cause, and nothing is created. Archived
    /// documents and versions whose text has not been extracted can be cited.
    ///
    /// The new version is number 1, has no parent, and is not approved and not the workspace default; both
    /// come later. No model is called. The response carries the `concurrencyToken` an edit will have to quote.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<BrandStyleGuideServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Create(
        string workspaceSlug,
        [FromBody] CreateBrandStyleGuideViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;

        var outcome = await guides.CreateAsync(userId, model, idempotencyKey, cancellationToken);

        // No Location: nothing reads a single guide yet. It arrives with the read route.
        return this.IdempotentResult(outcome, created => StatusCode(StatusCodes.Status201Created, created));
    }

    /// <summary>Reads one brand style guide: its working version and its active version.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="guideId">
    /// The guide to read. Constrained to a Guid, so a malformed id answers 404 at routing — the same status as
    /// an unknown guide and as another workspace's.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may read. `workingVersion` is the guide's highest version number — the one an edit builds
    /// on, approved or not. `activeVersion` is the workspace's default version, and is present only when that
    /// default is one of this guide's versions; otherwise it is null, with no fallback to the latest approved
    /// one. Both carry their sections, do and don't rules and cited source versions in full, so they repeat
    /// when the working version is the active one. Authors and approvers are not returned.
    ///
    /// An unknown id and another workspace's guide both answer `404 brand.guide.not_found`, deliberately
    /// indistinguishable. An archived guide reads normally. The response is `no-store`.
    /// </remarks>
    [HttpGet("{guideId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<BrandStyleGuideDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(string workspaceSlug, Guid guideId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await guides.GetAsync(guideId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}
