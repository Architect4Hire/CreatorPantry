using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Paging;
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

    /// <summary>Lists one brand style guide's versions, newest first.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="guideId">
    /// The guide whose history to list. Constrained to a Guid, so a malformed id answers 404 at routing — the
    /// same status as an unknown guide and as another workspace's.
    /// </param>
    /// <param name="query">Cursor and page size. Carries no workspace and no guide.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may read. Metadata only: a row says which version it is, whether it is `Draft` or
    /// `Approved`, how many sources it cites, who wrote it and why, when, and whether it is the workspace's
    /// active version. **No section, rule or body is returned** — read the guide itself for what a version
    /// says. Nothing is written: listing a version does not approve it or activate it.
    ///
    /// `staleSourceCount` is how many of the version's citations name a source document version the document
    /// has since been replaced past; `0` means every cited source is still its document's current version. A
    /// cited document the workspace has archived or removed is not counted — shelving a document does not
    /// change what the guide was written from.
    ///
    /// `isActive` marks the one version the workspace default points at, and is false on every row when the
    /// default is another guide's or the workspace has none. There is no fallback to the latest approved
    /// version. `createdByMembershipId` is a membership id, never a name or an address.
    ///
    /// Ordering is fixed at version number descending and there are no filters, so paging is `limit` plus the
    /// previous page's `nextCursor`, until `nextCursor` is null. A cursor is bound to the workspace and the
    /// guide it was issued for: replaying one against another guide answers `400
    /// brand.guide.invalid_request` rather than a plausible page of the wrong history. An out-of-range
    /// `limit` is clamped rather than refused.
    ///
    /// An unknown id and another workspace's guide both answer `404 brand.guide.not_found`, deliberately
    /// indistinguishable. An archived guide lists normally. The response is `no-store`.
    /// </remarks>
    [HttpGet("{guideId:guid}/versions")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<CursorPageServiceModel<BrandStyleGuideVersionSummaryServiceModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> ListVersions(
        string workspaceSlug,
        Guid guideId,
        [FromQuery] BrandStyleGuideVersionListViewModel query,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await guides.ListVersionsAsync(guideId, query, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Compares two of one brand style guide's versions and returns what differs between them.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="guideId">
    /// The guide whose versions to compare. Constrained to a Guid, so a malformed id answers 404 at routing —
    /// the same status as an unknown guide and as another workspace's.
    /// </param>
    /// <param name="query">Which two versions, by number. Carries no workspace and no guide.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may read. The comparison is calculated on the server from the two immutable versions and is
    /// the only one — a client renders it rather than deriving a second diff of its own, which is why the two
    /// versions' content is not returned alongside it. Nothing is written: comparing leaves no record that it
    /// happened, approves nothing and activates nothing, and no model is called.
    ///
    /// `from` and `to` are version numbers as the history lists them, not version ids: a number is unique only
    /// within its guide, so a number naming another guide's version matches nothing rather than being refused
    /// by a check. Either order is allowed — reading the newer version as `from` is what weighing a revert
    /// looks like — and `from` equal to `to` answers a comparison with no changes in it.
    ///
    /// Each part reports every item either version holds, including the unchanged ones, so a client renders a
    /// stable frame. `sections` are keyed by section key and channel and report `Added`, `Removed`, `Changed`
    /// or `Unchanged`, never `Moved`, because sections have no order. `rules` are identified by their kind and
    /// their own text, so they report `Added`, `Removed`, `Moved` or `Unchanged` and **never `Changed`**: a
    /// reworded rule is one removal and one addition, and its position is reported as a rank among rules of
    /// the same kind rather than as a stored sort order. `sources` are keyed by document, so re-pinning a
    /// citation to another version of the same document is one `Changed`.
    ///
    /// The guide's name, purpose and archived state are not compared: they belong to the guide rather than to
    /// any version of it.
    ///
    /// An unknown guide and another workspace's guide both answer `404 brand.guide.not_found`. A version
    /// number this guide does not have answers `404 brand.guide.version.not_found`, naming the parameter at
    /// fault — both parameters when both are wrong. The response is `no-store`.
    /// </remarks>
    // A literal segment inside the versions namespace, which also holds the history list above. A later
    // GET .../versions/{versionNumber} must carry a route constraint, or routing cannot tell it from this word.
    [HttpGet("{guideId:guid}/versions/compare")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<BrandStyleGuideVersionComparisonServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    // ValidationProblemDetails rather than ProblemDetails on the 404, as RecipesController.CompareVersions
    // declares it and unlike this controller's other reads: a version number this guide does not have names
    // the parameter at fault in `errors`, and that naming is the only reason
    // brand.guide.version.not_found is worth telling apart from brand.guide.not_found. One schema per status,
    // because a document cannot describe two and the wider one is the honest answer — every refusal this API
    // makes goes through CreateValidationProblemDetails, so `errors` is always present. On
    // brand.guide.not_found it is empty, which is what promises that the guide 404 names no parameter.
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> CompareVersions(
        string workspaceSlug,
        Guid guideId,
        [FromQuery] BrandStyleGuideVersionComparisonViewModel query,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await guides.CompareVersionsAsync(guideId, query, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}
