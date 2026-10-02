using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/brand-setup-session")]
public sealed class BrandSetupSessionController(IBrandSetupSessionFacade setupSession) : ControllerBase
{
    /// <summary>Returns the caller's own "Create my voice" setup session.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; the workspace is resolved server-side (tenancy.md).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Each user has at most one session per workspace, and sees only their own. `204 No Content` means the
    /// caller has none. The session is progress plus an opaque draft: it holds no style fields, and style
    /// guide versions remain the source of truth. The response is `no-store` and carries `ETag`, the same
    /// value as `rowVersion`.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<BrandSetupSessionServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(string workspaceSlug, CancellationToken cancellationToken)
    {
        var result = await setupSession.GetAsync(cancellationToken);
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

    /// <summary>Saves the caller's setup session, creating it on first save.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; the workspace is resolved server-side (tenancy.md).</param>
    /// <param name="model">The wizard position, step sets and opaque draft. Carries no user or workspace.</param>
    /// <param name="ifMatch">
    /// The `rowVersion` (or `ETag`) of the session this save was composed against. Omit it only to create.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Editor or above. Without `If-Match`, creates the session (`201`). Once a session exists `If-Match` is
    /// required: a missing or stale one answers `409 brand_setup_session_conflict` and the stored session is
    /// untouched. A completed session answers `409 brand_setup_session_completed`. `draftJson` must be a JSON
    /// object of at most 64 KB; it is stored as sent and never logged.
    /// </remarks>
    [HttpPut]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<BrandSetupSessionServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<BrandSetupSessionServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Save(
        string workspaceSlug,
        SaveBrandSetupSessionViewModel model,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        CancellationToken cancellationToken)
    {
        var result = await setupSession.SaveAsync(model, ifMatch, cancellationToken);
        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        var (session, created) = result.Value;
        Response.Headers.CacheControl = "no-store";
        WriteETag(session);

        return created
            ? Created($"/api/v1/workspaces/{workspaceSlug}/brand-setup-session", session)
            : Ok(session);
    }

    /// <summary>Marks the caller's setup session completed.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; the workspace is resolved server-side (tenancy.md).</param>
    /// <param name="ifMatch">The `rowVersion` (or `ETag`) of the session being completed. Required.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Editor or above. Requires `furthestStep` to be `finish` (`409 brand_setup_session_not_finished`
    /// otherwise). Completing only records that the walk-through finished: it activates no style guide,
    /// changes no guide, document or profile, and publishes nothing. A caller with no session answers
    /// `404 brand_setup_session_not_found`.
    /// </remarks>
    [HttpPost("complete")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<BrandSetupSessionServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Complete(
        string workspaceSlug,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        CancellationToken cancellationToken)
    {
        var result = await setupSession.CompleteAsync(ifMatch, cancellationToken);
        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        Response.Headers.CacheControl = "no-store";
        WriteETag(result.Value!);

        return Ok(result.Value);
    }

    /// <summary>Deletes the caller's own setup session ("start over").</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; the workspace is resolved server-side (tenancy.md).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Editor or above. Deletes only the caller's own session row; style guides, source documents, the brand
    /// profile and every other creator's session are untouched. Answers `204` whether or not a session existed;
    /// `409 brand_setup_session_conflict` if concurrent saves kept the session alive through the delete, in which
    /// case nothing was removed and the call can be repeated.
    /// </remarks>
    [HttpDelete]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Delete(string workspaceSlug, CancellationToken cancellationToken)
    {
        var result = await setupSession.DeleteAsync(cancellationToken);

        return result.Succeeded ? NoContent() : this.ProblemFor(result.Error!);
    }

    private void WriteETag(BrandSetupSessionServiceModel session) =>
        Response.Headers[HeaderNames.ETag] = $"\"{session.RowVersion}\"";
}
