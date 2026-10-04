using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>
/// The caller's own unsaved edit of one brand style guide: what the editor autosaves into.
/// </summary>
/// <remarks>
/// A sibling of the guide's own routes rather than part of them, because what it holds is not the guide. A
/// draft is one creator's scratch copy, private to them, and nothing is grounded on it.
/// </remarks>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/brand-style-guides/{guideId:guid}/edit-session")]
public sealed class BrandStyleGuideEditSessionController(IBrandStyleGuideEditSessionFacade sessions)
    : ControllerBase
{
    /// <summary>Returns the caller's own unsaved edit of this guide.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="guideId">
    /// The guide the draft belongs to. Constrained to a Guid, so a malformed id answers 404 at routing — the
    /// same status as an unknown guide and as another workspace's.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// **Editor or above**, the same bar as saving a version: a draft is only of use to someone who could keep
    /// it, and this is one creator's own unsaved work rather than part of the guide.
    ///
    /// Each creator has at most one draft per guide and sees only their own — there is no route, filter or role
    /// that returns somebody else's. `204 No Content` means the caller has none, which is the ordinary state
    /// before they have typed anything.
    ///
    /// `isStale` is the server's answer to whether the guide has gained a version since the draft was started.
    /// A stale draft is still returned in full: the creator's words are theirs, and what they need is to be
    /// told before they try to save. The response is `no-store` and carries `ETag`, the same value as
    /// `rowVersion`.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<BrandStyleGuideEditSessionServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(string workspaceSlug, Guid guideId, CancellationToken cancellationToken)
    {
        var result = await sessions.GetAsync(guideId, cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        Response.Headers.CacheControl = "no-store";

        if (result.Value is null)
        {
            return NoContent();
        }

        WriteETag(result.Value);

        return Ok(result.Value);
    }

    /// <summary>Keeps the caller's unsaved edit of this guide, creating it on first save.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="guideId">
    /// The guide being edited. Constrained to a Guid, so a malformed id answers 404 at routing.
    /// </param>
    /// <param name="model">The baseline version and the opaque draft. Carries no workspace, guide or user.</param>
    /// <param name="ifMatch">
    /// The `rowVersion` (or `ETag`) of the draft this save was composed against. Omit it only to create.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// **Editor or above.** Without `If-Match`, creates the draft (`201`). Once one exists `If-Match` is
    /// required: a missing or stale one answers `409 brand.guide.editSession.conflict` and the stored draft is
    /// untouched — one creator in two tabs, or an autosave that overlapped a discard.
    ///
    /// **Writes nothing the guide says.** This is scratch: no version, no approval, no activation, no audit
    /// entry, and no model call. The version a creator means to keep is written by
    /// `POST .../brand-style-guides/{guideId}/versions`, and **that write clears this draft** so "saved as
    /// version 4" and "you have unsaved changes" cannot both be true.
    ///
    /// **A draft is never refused for being behind.** `baselineVersionNumber` is stored as sent; if the guide
    /// has gained a version since, the reply says `isStale` rather than rejecting the request, because losing
    /// what a creator typed is the worse failure. The refusal that matters at the moment of writing belongs to
    /// the versions route, which quotes the same number.
    ///
    /// `draftJson` must be a JSON object of at most 512 KB — the guide's own ceilings allow over 300 KB of
    /// section text, so the cap sits above what a legitimate guide can hold. It is stored exactly as sent,
    /// never interpreted and never logged.
    /// </remarks>
    [HttpPut]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<BrandStyleGuideEditSessionServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<BrandStyleGuideEditSessionServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Save(
        string workspaceSlug,
        Guid guideId,
        [FromBody] SaveBrandStyleGuideEditSessionViewModel model,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        CancellationToken cancellationToken)
    {
        var result = await sessions.SaveAsync(guideId, model, ifMatch, cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        var (session, created) = result.Value;
        Response.Headers.CacheControl = "no-store";
        WriteETag(session);

        return created
            ? Created(
                $"/api/v1/workspaces/{workspaceSlug}/brand-style-guides/{guideId}/edit-session", session)
            : Ok(session);
    }

    /// <summary>Discards the caller's own unsaved edit of this guide.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="guideId">The guide whose draft to discard. Constrained to a Guid.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// **Editor or above.** Deletes only the caller's own draft row: every version of the guide, the guide
    /// itself and every other creator's draft are untouched. Answers `204` whether or not a draft existed,
    /// because what the request asked for is true either way.
    ///
    /// `409 brand.guide.editSession.conflict` if concurrent autosaves kept the draft alive through the delete,
    /// in which case nothing was removed and the call can be repeated.
    /// </remarks>
    [HttpDelete]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Delete(
        string workspaceSlug, Guid guideId, CancellationToken cancellationToken)
    {
        var result = await sessions.DeleteAsync(guideId, cancellationToken);

        return result.Succeeded ? NoContent() : this.ProblemFor(result.Error!);
    }

    private void WriteETag(BrandStyleGuideEditSessionServiceModel session) =>
        Response.Headers[HeaderNames.ETag] = $"\"{session.RowVersion}\"";
}
