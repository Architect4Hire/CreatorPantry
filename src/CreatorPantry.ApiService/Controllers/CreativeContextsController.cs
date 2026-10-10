using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/creative-contexts")]
public sealed class CreativeContextsController(ICreativeContextFacade contexts) : ControllerBase
{
    /// <summary>Lists the workspace's live creative contexts, most recently updated first.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="query">The cursor and page size. Carries no workspace.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer may read. Cursor-paged: follow `nextCursor` until it is null. A cursor
    /// is bound to the workspace it was issued for — `content.creative_context.cursor.invalid` says to start
    /// again without one. `limit` is clamped rather than refused.
    ///
    /// **Archived contexts are not listed**; they remain readable by id. Each row is a summary — title,
    /// channels, day, theme and how many sources it names — and never the picture description, which the
    /// detail route publishes. Because the order is by last update, a context edited while a client pages can
    /// move to the front; a client that needs every row re-lists rather than trusting a long-held cursor.
    /// The response is `no-store`: it is workspace-private creator content.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<CursorPageServiceModel<CreativeContextSummaryServiceModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> List(
        string workspaceSlug,
        [FromQuery] CreativeContextListViewModel query,
        CancellationToken cancellationToken)
    {
        // Set before the result is examined, so a refusal carries it too.
        Response.Headers.CacheControl = "no-store";

        var result = await contexts.ListRecentAsync(query, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Reads one creative context of the workspace named by the route, in full.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the list route.</param>
    /// <param name="contextId">
    /// The context to read. Constrained to a Guid, so a malformed id never reaches this action: routing answers
    /// 404 with the edge's generic <c>not_found</c> code.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer may read. An archived context is still readable here.
    ///
    /// **A reference is ids and nothing else.** It carries no title, alt text, preview or availability: a
    /// context points at its sources and never copies them, so resolve an id through its own route to show it.
    /// A source that has since been archived, deleted, declined or expired is still listed — the reference is
    /// the record that the work used it.
    ///
    /// `concurrencyToken` is opaque; send it back as `expectedConcurrencyToken` on the next edit. An unknown id
    /// answers `404 content.creative_context.not_found`, and **another workspace's context answers exactly
    /// that** — the same code, sentence and body. The response is `no-store`.
    /// </remarks>
    [HttpGet("{contextId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<CreativeContextServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(string workspaceSlug, Guid contextId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await contexts.GetAsync(contextId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>The pictures this piece of work names, and what is known about what each shows.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the list route.</param>
    /// <param name="contextId">The piece of work.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer may read. A piece of work that names no picture is an empty list, not a
    /// `404`; a context this workspace does not have answers
    /// `404 content.creative_context.not_found`, the same answer another workspace's gets.
    ///
    /// **A picture that no longer resolves is left out**, not listed as unavailable: a declined or expired
    /// generated image and a removed library asset cannot be shown, so there is nothing to offer about them.
    ///
    /// Each entry carries the ids its own render route takes, whether the creator kept it or chose it, their
    /// own alt text where they wrote any, and `reading` — what a model saw in the picture, with each
    /// observation's `confidence` beside its text, or null when nobody has looked.
    ///
    /// **A `reading` is not the creator's words.** It is model prose about a photograph that no creator has
    /// reviewed; a surface showing one says so and shows every confidence, because an observation whose label
    /// is dropped reads as a fact. It is never presented as alt text.
    ///
    /// The response is `no-store`: it is workspace-private creator content.
    /// </remarks>
    [HttpGet("{contextId:guid}/pictures")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<IReadOnlyList<CreativeContextPictureServiceModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> GetPictures(
        string workspaceSlug, Guid contextId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await contexts.ListPicturesAsync(contextId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Starts a creative context, optionally from one source.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the list route.</param>
    /// <param name="model">The creator's words, channels, day, theme and an optional source. All optional.</param>
    /// <param name="idempotencyKey">
    /// Optional. Replaying a key with the same body returns the context the first call created, with
    /// <c>Idempotency-Replayed: true</c>; the same key with a different body answers
    /// <c>422 idempotency.key_reused</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above. **`from` makes a hand-off one call**: send the source a creator chose on one
    /// screen and open the next screen with the id this returns.
    ///
    /// A source is resolved inside this workspace before anything is stored. One that does not exist, belongs
    /// to another workspace, or is archived, deleted, declined or expired answers
    /// `422 content.creative_context.reference.unprocessable` — **one code and one sentence for all of them**,
    /// so an id cannot be used to ask what another workspace holds. `SocialPackage` is not accepted yet and is
    /// a `400`.
    ///
    /// A channel key that names no channel, or one that has been retired, answers
    /// `422 content.creative_context.channel.unprocessable`; a theme key that is not one of this workspace's
    /// live themes answers `422 content.creative_context.theme.unprocessable`. Blank text is stored as absent.
    /// `409 content.creative_context.conflict` means the write itself failed and nothing was stored; retry it.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<CreativeContextServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Create(
        string workspaceSlug,
        CreateCreativeContextViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await contexts.CreateAsync(userId, model, idempotencyKey, cancellationToken);

        // The location is the only thing this layer contributes, because only it knows the route shape.
        return this.IdempotentResult(outcome, created =>
            Created($"/api/v1/workspaces/{workspaceSlug}/creative-contexts/{created.Id}", created));
    }

    /// <summary>Edits a creative context's words, channels, day, theme or archived state.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the list route.</param>
    /// <param name="contextId">The context to edit.</param>
    /// <param name="model">The fields to change, and the token of the read they were composed against.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above. **A field left out is untouched; a field sent as `null` is cleared.**
    /// `channelKeys` replaces the ordered set. `archived: true` takes the context out of the recent list and
    /// `false` puts it back; there is no delete.
    ///
    /// `expectedConcurrencyToken` is required. When the context has changed since that read the answer is
    /// `409 content.creative_context.stale.conflict` and **nothing is written** — keep what the creator typed,
    /// re-read, and send again. A token that could never have been issued is a `400`. An edit that changes
    /// nothing writes nothing and returns the same token.
    ///
    /// Only a newly chosen channel or theme is checked against the catalogue: one this context already holds
    /// may stay after it is retired, so retiring a channel never makes existing work uneditable. Sources are
    /// changed through the references routes, not here.
    /// </remarks>
    [HttpPatch("{contextId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<CreativeContextServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Patch(
        string workspaceSlug,
        Guid contextId,
        PatchCreativeContextViewModel model,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var result = await contexts.PatchAsync(userId, contextId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Adds one source to a creative context, after those it already names.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the list route.</param>
    /// <param name="contextId">The context to add to.</param>
    /// <param name="model">The source, and the token of the read this was composed against.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above. Returns the whole context, so the client holds the next token.
    ///
    /// The source is resolved exactly as on create, with the same single
    /// `422 content.creative_context.reference.unprocessable` for every way it can fail to be usable. A source
    /// this context already names answers `409 content.creative_context.reference.duplicate.conflict` — a
    /// recipe is one source whichever version is pinned. A context names at most 20 sources;
    /// the next is `422 content.creative_context.reference_limit.unprocessable`.
    ///
    /// `expectedConcurrencyToken` is required and a stale one is
    /// `409 content.creative_context.stale.conflict`, as on the edit route. **Retrying after a lost response
    /// therefore cannot add the source twice**: the first call moved the token.
    /// </remarks>
    [HttpPost("{contextId:guid}/references")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<CreativeContextServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> AddReference(
        string workspaceSlug,
        Guid contextId,
        AddCreativeContextReferenceViewModel model,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await contexts.AddReferenceAsync(contextId, model, cancellationToken);

        return result.Succeeded
            ? Created($"/api/v1/workspaces/{workspaceSlug}/creative-contexts/{contextId}", result.Value)
            : this.ProblemFor(result.Error!);
    }

    /// <summary>Stops a creative context naming a source.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the list route.</param>
    /// <param name="contextId">The context to change.</param>
    /// <param name="referenceId">The reference to remove — its own <c>id</c>, not the id of what it names.</param>
    /// <param name="expectedConcurrencyToken">The <c>concurrencyToken</c> of the read this was composed against.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above. **Only the reference goes; the recipe, picture or prompt it named is
    /// untouched.** Returns the whole context. The remaining references keep their `sortOrder`, so the order
    /// is unchanged and the numbers may have a gap.
    ///
    /// The token is a query parameter because a `DELETE` carries no body. A stale one is
    /// `409 content.creative_context.stale.conflict`; an unknown context or reference, or another workspace's,
    /// is `404 content.creative_context.not_found`.
    /// </remarks>
    [HttpDelete("{contextId:guid}/references/{referenceId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<CreativeContextServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> RemoveReference(
        string workspaceSlug,
        Guid contextId,
        Guid referenceId,
        [FromQuery] string? expectedConcurrencyToken,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await contexts.RemoveReferenceAsync(
            contextId, referenceId, expectedConcurrencyToken, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}
