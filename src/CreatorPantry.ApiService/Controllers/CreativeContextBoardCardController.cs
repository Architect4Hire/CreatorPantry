using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/creative-contexts/{contextId:guid}/board-card")]
public sealed class CreativeContextBoardCardController(IContentCardFacade cards) : ControllerBase
{
    /// <summary>Reads the card the Content Pipeline put on the board for one piece of work.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="contextId">The piece of work.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer may read. Answers the card in full — the card detail route's shape.
    ///
    /// **`404 content.card.not_found` means the work has no such card**, and is the answer a resumed run reads
    /// as "not on the board yet". It is also, word for word, the answer for a piece of work that does not
    /// exist and for one another workspace owns: this route cannot be used to ask whether either is there.
    /// Cards a creator put on the board by hand for the same work are not this card and are not returned.
    /// The response is `no-store`.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<ContentCardDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(string workspaceSlug, Guid contextId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await cards.GetPipelineCardAsync(contextId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Puts a Content Pipeline run on the content board as one card.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the read route.</param>
    /// <param name="contextId">The piece of work the run is about.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above; a Viewer is refused with `403 content.card.forbidden`. **There is no body.**
    /// The card is made from what the piece of work holds now: it links the work, the recipe the work is about
    /// and the version it pinned, the work's posts when at least one channel's post stands accepted, the first
    /// kept picture that is in the library, and the prompt saved with that picture. Its title is the work's
    /// working title, or the recipe's. It goes to the bottom of `ToDo`. Nothing is copied, and **nothing is
    /// scheduled or published**: a card is a plan.
    ///
    /// **This is the last step of a run, and it saves nothing but the card.** Each kept picture is saved to
    /// the library, with its prompt, by the keep route — once each, however often that is repeated. So a run
    /// that stopped part-way is finished by sending again whatever has not been done: no step undoes another,
    /// and no partial state is wrong.
    ///
    /// **Safe to send again, at any time, with no key.** A piece of work has at most one such card, and the
    /// database keeps that rule. The first call answers `201` with the card and its `Location`; every later
    /// call — a retry after a lost response, a resumed run a week on, a second request racing the first —
    /// answers `200` with that same card and writes nothing. Deleting the card frees the work to be put on
    /// the board again; so does an edit that points the card at a different piece of work.
    ///
    /// A work with no picture saved to the library answers `422 content.card.pipeline.unprocessable` naming
    /// `keepers`. A recipe that has been archived, a pinned version that can no longer be read and posts that
    /// are not accepted are left off the card rather than refusing it.
    ///
    /// An unknown piece of work answers `404 content.creative_context.not_found`, and so — identically — does
    /// one another workspace owns, and an archived one that has no card yet. A work archived after its card
    /// was made still answers `200` with that card: a repeat is told what happened. `409 content.card.conflict` means the write did not
    /// happen and is safe to send again. The response is `no-store`.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<ContentCardDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ContentCardDetailServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Commit(string workspaceSlug, Guid contextId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var result = await cards.CommitPipelineAsync(userId, contextId, cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        var outcome = result.Value!;

        // The status is the only place "this call made it" is said; the body is the card either way.
        return outcome.Created
            ? Created($"/api/v1/workspaces/{workspaceSlug}/content-cards/{outcome.Card.Card.Id}", outcome.Card)
            : Ok(outcome.Card);
    }
}
