using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/recipes/{recipeId:guid}/ai-proposals")]
public sealed class AiProposalsController(IAiProposalFacade proposals) : ControllerBase
{
    /// <summary>Asks for an AI proposal against one version of a recipe.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs; nothing here reads it, and no request field may name one
    /// (tenancy.md).
    /// </param>
    /// <param name="recipeId">The recipe to work on. From the route, never from the body.</param>
    /// <param name="model">
    /// An allow-listed task discriminator, a scope, and the source version. It cannot carry a prompt, a
    /// template, a model, a provider parameter or a tool list — see <see cref="RequestAiProposalViewModel"/>.
    /// </param>
    /// <param name="idempotencyKey">
    /// Required. Generation spends a provider budget, so a repeat of the same key and the same request returns
    /// the original with <c>Idempotent-Replayed: true</c> rather than buying a second answer.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// <c>202</c> rather than <c>201</c>: nothing has been generated yet. The request is durable and the model
    /// is called by the worker afterwards, so the API does not stay open around unbounded provider work
    /// (api-contract.md). The <c>Location</c> header points at the status resource to poll.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<AiProposalStatusServiceModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Request(
        string workspaceSlug,
        Guid recipeId,
        RequestAiProposalViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var outcome = await proposals.RequestAsync(recipeId, model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, accepted => Accepted(
            $"/api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/ai-proposals/{accepted.AiProposalRequestId}",
            accepted));
    }

    /// <summary>Where a requested proposal has got to, and the proposal itself once there is one.</summary>
    /// <param name="aiProposalId">
    /// The id the request returned. It is the operation's id: a request exists from the moment it is accepted,
    /// long before a proposal does, so there would be nothing else to poll. The proposal carries its own
    /// identity inside the reply for a disposition to act on.
    /// </param>
    /// <remarks>
    /// Polling before the proposal exists is a <c>200</c> carrying a status, not a <c>404</c> — the request
    /// resource exists from the moment it was accepted, and reporting it missing would make a normal poll look
    /// like a failure.
    /// </remarks>
    [HttpGet("{aiProposalId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<AiProposalStatusServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(
        string workspaceSlug,
        Guid recipeId,
        Guid aiProposalId,
        CancellationToken cancellationToken)
    {
        var result = await proposals.GetAsync(recipeId, aiProposalId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Records what the creator decided about a proposal, and applies what they accepted.</summary>
    /// <param name="aiProposalId">
    /// The same id <see cref="Get"/> takes — the request's. An operation has at most one proposal, so the request
    /// identifies it without ambiguity, and one route segment meaning two different identities would be a trap.
    /// </param>
    /// <param name="model">
    /// The decision, and the confirmation naming the changes being accepted. Nothing in it can describe a change:
    /// only the ids of changes the server itself computed and stored.
    /// </param>
    /// <remarks>
    /// <para>
    /// <c>200</c> rather than <c>202</c>: unlike requesting a proposal, this finishes before it returns. There is
    /// no provider call — the answer was generated long ago — and the recipe version, the per-change decisions,
    /// the feedback and the operation's terminal status all commit in one transaction before the reply is written.
    /// </para>
    /// <para>
    /// <c>Contributor</c> at the edge, because accepting changes writes a recipe version. A <c>Viewer</c> cannot
    /// reject either, which is the deliberate cost of one policy per route: rejecting decides the fate of a
    /// workspace's content, and the role that may not change a recipe should not be the role that closes a
    /// proposal against it.
    /// </para>
    /// <para>
    /// Retrying is safe and needs no <c>Idempotency-Key</c>. A proposal can be decided once; a repeat of the same
    /// decision is recognised and answered without writing anything, and a repeat asking for a
    /// <em>different</em> decision is a <c>409</c> rather than a second version of the recipe.
    /// </para>
    /// </remarks>
    [HttpPost("{aiProposalId:guid}/disposition")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<AiProposalDispositionServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Disposition(
        string workspaceSlug,
        Guid recipeId,
        Guid aiProposalId,
        AiProposalDispositionViewModel model,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;

        var result = await proposals.DispositionAsync(
            userId, recipeId, aiProposalId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}
