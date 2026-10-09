using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>
/// IMG-004: a structured reading of a reference photograph the creator uploaded, and an editable prompt drawn
/// from it.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/reference-image-requests")]
public sealed class ReferenceImageRequestsController(IAiReferenceImageRequestFacade requests) : ControllerBase
{
    /// <summary>Reads one reference image and composes a prompt from what it shows.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs (tenancy.md).
    /// </param>
    /// <param name="model">
    /// Which picture to read, and an optional note about what the creator is looking for. It carries no
    /// bytes, no media type, no model, no provider and no rendering setting; see
    /// <see cref="RequestReferenceImageAnalysisViewModel"/>.
    /// </param>
    /// <param name="idempotencyKey">
    /// Required. A vision call is the most expensive kind this product makes, so a repeat of the same key
    /// returns the original with `Idempotent-Replayed: true` rather than buying a second reading.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor or above, the bar for spending the workspace's allowance. `202` rather than `201`: nothing
    /// is read yet, and the model is called by the worker afterwards (api-contract.md). The `Location` header
    /// points at the status resource to poll.
    ///
    /// **The image is named, not uploaded here.** It is a picture this workspace already holds, and `source`
    /// says which kind: `BrandDocument` with `referenceDocumentId`, `DamAsset` with `mediaAssetId` (and
    /// optionally `mediaAssetVersionNumber`), or `GeneratedImage` with `generatedImageId`. Send exactly one
    /// source and only that source's ids; anything else is a `400` naming the field. A body carrying only
    /// `referenceDocumentId`, with no `source`, is a `BrandDocument` — the shape this route took before it
    /// accepted the other two.
    ///
    /// JPEG, PNG, WEBP and GIF are readable. A picture that is anything else, one too large to send, one this
    /// workspace does not have, a removed asset, a version the asset does not have, a declined or expired
    /// generated image, and one that does not exist all answer `404 ai.referenceImage.not_found` — one answer
    /// for every source, because each is a client naming something it was not shown.
    ///
    /// **The version is pinned when the request is made.** The bytes the model reads are the ones named,
    /// even if the document or the asset is replaced in the minutes before the worker claims the operation.
    /// An asset named without a version is pinned to its current one.
    ///
    /// **A reading of a library asset or a generated image may be kept with that picture**, so that later
    /// work on it need not look twice. Only what was observed is kept — never the prompt — and only for a
    /// request with no note, about a picture with a single frame.
    ///
    /// **The image is untrusted material, and so is the note.** Both are sent as material to describe rather
    /// than as instructions. Writing photographed into the image is described as part of what the picture
    /// shows; it is not obeyed.
    ///
    /// **What the answer will not contain.** No identification of a person, no claim about who owns a brand,
    /// a product or the photograph, and no statement about the food's safety, nutrition, allergens or dietary
    /// suitability — each is refused rather than returned. Every observation carries its own confidence
    /// (`Clear`, `Probable`, `Unclear`), because a confident misreading of someone's reference is worse than
    /// an uncertain one. An animated GIF is read as its opening frame, with a warning saying so.
    ///
    /// **Nothing is rendered and no recipe changes.** The answer is observations plus one prompt and a list
    /// of things to avoid; it proposes no recipe edit, and the creator's edit of the prompt is what becomes
    /// authoritative when they save it to their library.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<AiProposalStatusServiceModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    public async Task<IActionResult> Request(
        string workspaceSlug,
        RequestReferenceImageAnalysisViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var outcome = await requests.RequestAsync(model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, accepted => Accepted(
            $"/api/v1/workspaces/{workspaceSlug}/reference-image-requests/{accepted.AiProposalRequestId}",
            accepted));
    }

    /// <summary>Where a requested reading has got to, and its observations and prompt once there are some.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed, as on the request above.</param>
    /// <param name="requestId">The id the request returned. It is the operation's id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer may read. Polling before the reading exists is a `200` carrying a
    /// status, not a `404`. An unknown id, another workspace's request and one that ran a different task all
    /// answer `404 ai.referenceImageRequest.not_found`.
    /// </remarks>
    [HttpGet("{requestId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<AiProposalStatusServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(
        string workspaceSlug, Guid requestId, CancellationToken cancellationToken)
    {
        var result = await requests.GetAsync(requestId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}
