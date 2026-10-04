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
    /// come later. No model is called. The response carries the guide's `concurrencyToken`, which covers the
    /// guide row — its name, purpose and archived state. An **edit quotes `expectedWorkingVersionNumber`**
    /// instead, because writing a version deliberately leaves that row alone and so never moves its token.
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

    /// <summary>Saves a creator's edit of a brand style guide as one further immutable version.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="guideId">
    /// The guide to write to. Constrained to a Guid, so a malformed id answers 404 at routing — the same status
    /// as an unknown guide and as another workspace's.
    /// </param>
    /// <param name="model">
    /// The submitted change: the working version it was made against, an optional reason, and the sections,
    /// rules and citations to set, clear or drop. Carries no workspace, no guide and no version to write.
    /// </param>
    /// <param name="idempotencyKey">
    /// Required. Replaying a request with the same key returns the version the first one wrote, with
    /// `Idempotency-Replayed: true`, and writes nothing further; the same key with a different body answers
    /// `422 idempotency.key_reused`.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// **Editor or above**, the role that creates a guide. Approving the version this writes, and making it the
    /// workspace default, remain separate decisions behind their own gates — this one approves nothing and
    /// activates nothing, and no model is called.
    ///
    /// **A submitted change, not a replacement guide.** An omitted collection is untouched, so a request naming
    /// one section says nothing about the others. A section is set or replaced by being named with text, and
    /// **cleared by being named with none** — the one difference from creation, where a blank answer is simply
    /// no answer. `rules` is whole-list, because a rule has no identity beyond its own text and the order is the
    /// creator's: send `items` in full to replace them, an empty array to clear them, or leave `rules` out to
    /// keep them. `rules: {}` without `items` is a `400` rather than a way to delete them all. Citations are
    /// added and dropped by exact `(documentId, versionNumber)`, so re-pinning one to a document's newer version
    /// is an `uncite` and a `cite` in the same request. A request that asks for nothing at all is a `400`.
    ///
    /// **`expectedWorkingVersionNumber` is the concurrency guard**, checked inside the writing transaction
    /// against the guide as it actually stands: a mismatch answers `409 brand.guide.workingVersion.conflict`
    /// and names both numbers in `expectedWorkingVersionNumber` and `workingVersionNumber`. It is not the
    /// guide's `concurrencyToken` — that covers the guide row, which a version write deliberately leaves alone.
    ///
    /// **A change that changes nothing writes nothing**: `200 OK` with `versionId` and `versionNumber` null,
    /// reporting the version that still stands, and no audit entry. A version written answers `201 Created`
    /// with its number. Text is stored exactly as typed, and the parent version is untouched — editing an
    /// approved version leaves its approval intact and writes a new draft beside it.
    ///
    /// Three refusals are about what the request would produce: past one of the guide's ceilings is `400
    /// brand.guide.version.limit.invalid_request`, naming which; a cited document version that cannot be used —
    /// never issued, another workspace's, removed, or one the document does not have — is `422
    /// brand.guide.source.unprocessable`, identically for every cause, listing each in `unusableSources`
    /// (including a citation inherited from the working version, which is why they are named by document and
    /// version rather than by request index); and a version of an archived guide is `409
    /// brand.guide.archived.conflict`. An unknown guide and another workspace's both answer `404
    /// brand.guide.not_found`.
    /// </remarks>
    [HttpPost("{guideId:guid}/versions")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<BrandStyleGuideVersionSavedServiceModel>(StatusCodes.Status201Created)]
    // The no-op, and declared because it is a success a client has to be able to tell from a write: nothing
    // changed, so there is no version and nothing to approve.
    [ProducesResponseType<BrandStyleGuideVersionSavedServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    // ValidationProblemDetails on the 409 for the reason Activate records: the working-version conflict names
    // the field at fault in `errors`' sibling extensions and is the refusal a client acts on.
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> SaveVersion(
        string workspaceSlug,
        Guid guideId,
        [FromBody] SaveBrandStyleGuideVersionViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;

        var outcome = await guides.SaveVersionAsync(userId, guideId, model, idempotencyKey, cancellationToken);

        // No Location on either answer: the module has no per-version read route, and the guide read reports
        // the working version in full.
        return this.IdempotentResult(
            outcome,
            saved => saved.VersionId is null ? Ok(saved) : StatusCode(StatusCodes.Status201Created, saved));
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

    /// <summary>Approves one brand style guide version: marks it finished, so an Owner may activate it.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="guideId">
    /// The guide whose version to approve. Constrained to a Guid, so a malformed id answers 404 at routing —
    /// the same status as an unknown guide and as another workspace's.
    /// </param>
    /// <param name="versionNumber">
    /// Which of the guide's versions, by number as the history lists them. Constrained to an int, which is
    /// what keeps this route distinct from the literal `compare` segment above.
    /// </param>
    /// <param name="model">The confirmation and an optional reason.</param>
    /// <param name="idempotencyKey">
    /// Required. Replaying a request with the same key returns the approval the first one recorded, with
    /// `Idempotency-Replayed: true`, and writes nothing further; the same key with a different body answers
    /// `422 idempotency.key_reused`. The key is scoped to the workspace, so the same one in another workspace
    /// is another request rather than a replay.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// **Editor or above**, the role that creates and edits a guide — and deliberately below the Owner that
    /// activation asks for. Saying a version is finished is the authoring decision; choosing what the whole
    /// workspace writes with is not, and keeping them apart is what lets an Editor hand finished work over
    /// without repointing the workspace default themselves.
    ///
    /// `confirmed` must be `true` — a well-formed body is not by itself a decision. There is no expected-state
    /// field: an approval is write-once and names one version, so there is nothing it could be racing to
    /// replace. **And there is no unapprove.** An approval is never withdrawn, so every generation that cited
    /// a version stays traceable to a version that was approved when it was used; moving off one means
    /// approving and activating another.
    ///
    /// Nothing in the guide is edited — the version is immutable and this adds a row beside it — and **nothing
    /// is activated**: the workspace default still needs an Owner and a separate request. Approving a version
    /// that is already approved is a success that writes nothing and answers `alreadyApproved: true`, with the
    /// original `approvedAt` and approver rather than this request's.
    ///
    /// Two refusals are about the version named: a version of an archived guide is `409
    /// brand.guide.archived.conflict`, and one with no sections and no rules is `409
    /// brand.guide.version.empty.conflict`. **Stale citations are not among them** — a version citing a source
    /// document replaced since can be approved, because approval is about the wording being finished; it is
    /// activation that refuses to ground a workspace on it. Two requests approving the same version at once
    /// answer `409 brand.guide.version.approval.conflict` to the one that lost; asking again reports the
    /// approval that was recorded.
    ///
    /// An unknown guide and another workspace's both answer `404 brand.guide.not_found`. A version number this
    /// guide does not have answers `404 brand.guide.version.not_found`.
    /// </remarks>
    [HttpPost("{guideId:guid}/versions/{versionNumber:int}/approval")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<BrandStyleGuideApprovalResultServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    // ValidationProblemDetails on the 404 for the reason CompareVersions records: a version number this guide
    // does not have names the route segment at fault in `errors`.
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    // Declared because this route can return it: the same key with a different body is `idempotency.key_reused`,
    // as the source library's routes document. A client has to be able to tell that from the 409s above, which
    // are about the version rather than about the request having already been used for something else.
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Approve(
        string workspaceSlug,
        Guid guideId,
        int versionNumber,
        [FromBody] ApproveBrandStyleGuideVersionViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;

        var outcome = await guides.ApproveVersionAsync(
            userId, guideId, versionNumber, model, idempotencyKey, cancellationToken);

        // No Location: an approval has no read route of its own, and the guide read already reports it.
        return this.IdempotentResult(outcome, Ok);
    }

    /// <summary>Makes one approved brand style guide version this workspace's default.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="guideId">
    /// The guide whose version to activate. Constrained to a Guid, so a malformed id answers 404 at routing —
    /// the same status as an unknown guide and as another workspace's.
    /// </param>
    /// <param name="versionNumber">
    /// Which of the guide's versions, by number as the history lists them. Constrained to an int, which is
    /// what keeps this route distinct from the literal `compare` segment above.
    /// </param>
    /// <param name="model">The confirmation, the expected current active version, and an optional reason.</param>
    /// <param name="idempotencyKey">
    /// Required. Replaying a request with the same key returns the activation the first one recorded, with
    /// `Idempotency-Replayed: true`, and writes nothing further; the same key with a different body is refused.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// **Owner only**, above the Editor that creates and edits a guide: there is one default per workspace and
    /// it governs what every later generation is grounded on.
    ///
    /// `confirmed` must be `true` — a well-formed body is not by itself a decision. `expectedActiveVersionId`
    /// is the version the caller believes holds the default now, from `activeVersion.id` on the guide read or
    /// the history row whose `isActive` is true; **omit it only to assert the workspace has no default**, which
    /// is checked rather than assumed, so forgetting the field cannot replace a default the caller never saw. A
    /// mismatch answers `409 brand.guide.activation.conflict` and names what actually holds it in
    /// `activeGuideId`, `activeVersionId` and `activeVersionNumber` — each null when the workspace has none.
    ///
    /// Nothing in the guide is edited: the version is immutable and this writes only the workspace's one
    /// activation decision, its audit entry, and nothing else. `200 OK` rather than `201`, because the default
    /// is a singleton being repointed and not a new subresource. Activating the version that already holds it
    /// is a success that writes nothing and answers `alreadyActive: true`, with the original `activatedAt` and
    /// activator rather than this request's.
    ///
    /// Four refusals are about the version named and will not read differently after a re-read, so they are
    /// answered before the expectation is checked: a version with no approval is `409
    /// brand.guide.version.unapproved.conflict`; one citing a source document that has been replaced since is
    /// `409 brand.guide.version.stale.conflict`, with `staleSourceCount`; one with no sections and no rules is
    /// `409 brand.guide.version.empty.conflict`; and a version of an archived guide is `409
    /// brand.guide.archived.conflict`. There is no override: lifting one of these is a policy decision rather
    /// than a field on a request.
    ///
    /// An unknown guide and another workspace's both answer `404 brand.guide.not_found`. A version number this
    /// guide does not have answers `404 brand.guide.version.not_found`.
    /// </remarks>
    [HttpPost("{guideId:guid}/versions/{versionNumber:int}/activation")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceOwner)]
    [ProducesResponseType<BrandStyleGuideActivationResultServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    // ValidationProblemDetails on the 404 for the reason CompareVersions records: a version number this guide
    // does not have names the route segment at fault in `errors`, and that naming is the only reason
    // brand.guide.version.not_found is worth telling apart from brand.guide.not_found.
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Activate(
        string workspaceSlug,
        Guid guideId,
        int versionNumber,
        [FromBody] ActivateBrandStyleGuideVersionViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;

        var outcome = await guides.ActivateVersionAsync(
            userId, guideId, versionNumber, model, idempotencyKey, cancellationToken);

        // No Location: the activation is not a resource of its own, and the guide read already reports it.
        return this.IdempotentResult(outcome, Ok);
    }
}
