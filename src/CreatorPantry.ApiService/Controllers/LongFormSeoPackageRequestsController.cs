using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using CreatorPantry.ApiService.RateLimiting;
using CreatorPantry.Domain.Managers.Limits;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>
/// LF.5.3: request and follow an SEO package (title, slug, meta description, key phrases, internal-link ideas)
/// for an accepted long-form draft. The same capability as <see cref="SeoPackageRequestsController"/> with a
/// different subject: advisory, it changes nothing about the piece and invents no metric.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/long-form-drafts/{draftId:guid}/seo-package-requests")]
public sealed class LongFormSeoPackageRequestsController(IAiSeoPackageRequestFacade requests) : ControllerBase
{
    /// <summary>Asks for an SEO package written about an accepted long-form draft.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs (tenancy.md).
    /// </param>
    /// <param name="draftId">The draft to write about. Another workspace's is simply not there.</param>
    /// <param name="model">The accepted revision, and optionally which sections to write.</param>
    /// <param name="idempotencyKey">Required: a retried request returns the first answer and spends nothing more.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Queued, not run: `202` with the request to follow. The package is written against the article's own
    /// text — its title, an issue's note and the written sections — and is a set of recommendations for the
    /// creator to read and use as they see fit. Nothing is applied to the piece, and nothing is published.
    ///
    /// `sections` names any of `seoTitle`, `metaDescription`, `keyPhrases`, `slug` and `internalLinks`; omitted
    /// or empty means all five. There is no `altText` for an article — it carries no image the package has
    /// been given a fact about — and naming it, an unknown section, the same section twice or the slug alone
    /// answers `400 ai.seoPackage.invalid_request`. The slug is derived from the proposed title by rule, never
    /// written by a model. An internal-link idea names one of the workspace's approved recipes.
    ///
    /// Only an accepted piece is written about: any other, including one accepted and since marked for review,
    /// answers `409 ai.seoPackage.longFormDraft.not_accepted.conflict`. `revisionNumber` must be the accepted
    /// revision; another answers `400 ai.seoPackageLongFormRevision.invalid_request`. An unknown draft answers
    /// `404 ai.seoPackage.longFormDraft.not_found`. A deployment with the task switched off answers
    /// `400 ai.seoPackage.not_enabled`, and an account that cannot pay for it is refused before anything is
    /// queued.
    /// </remarks>
    [OperationLimit(LimitedOperation.AiRequest)]
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<AiProposalStatusServiceModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    public async Task<IActionResult> Request(
        string workspaceSlug,
        Guid draftId,
        RequestLongFormSeoPackageViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var outcome = await requests.RequestForLongFormAsync(draftId, model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, accepted => Accepted(
            $"/api/v1/workspaces/{workspaceSlug}/long-form-drafts/{draftId}/seo-package-requests/"
                + accepted.AiProposalRequestId,
            accepted));
    }

    /// <summary>Reads one SEO package request for a long-form draft, with its package once there is one.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed (tenancy.md).</param>
    /// <param name="draftId">The draft the request was made for.</param>
    /// <param name="requestId">The request, as the `202` named it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer. One `404 ai.seoPackageRequest.not_found` for a request that does not
    /// exist, is another workspace's, is about another draft or a recipe, or ran a different task.
    /// </remarks>
    [HttpGet("{requestId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<AiProposalStatusServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(
        string workspaceSlug, Guid draftId, Guid requestId, CancellationToken cancellationToken)
    {
        var result = await requests.GetForLongFormAsync(draftId, requestId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}
