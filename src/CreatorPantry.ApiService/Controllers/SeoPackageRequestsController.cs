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
/// RCPUB-002: request and follow an SEO package (title, slug, meta description, key phrases, alt-text
/// suggestions, internal-link ideas) for one approved recipe version. Advisory: it proposes no change to the
/// recipe and invents no metric. Accepting the result as content is a separate seam.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/recipes/{recipeId:guid}/seo-package-requests")]
public sealed class SeoPackageRequestsController(IAiSeoPackageRequestFacade requests) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<AiProposalStatusServiceModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    public async Task<IActionResult> Request(
        string workspaceSlug,
        Guid recipeId,
        RequestSeoPackageViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var outcome = await requests.RequestAsync(recipeId, model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, accepted => Accepted(
            $"/api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/seo-package-requests/"
                + accepted.AiProposalRequestId,
            accepted));
    }

    [HttpGet("{requestId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<AiProposalStatusServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(
        string workspaceSlug, Guid recipeId, Guid requestId, CancellationToken cancellationToken)
    {
        var result = await requests.GetAsync(recipeId, requestId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}
