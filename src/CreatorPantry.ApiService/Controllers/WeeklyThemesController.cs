using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/weekly-themes")]
public sealed class WeeklyThemesController(IWorkspaceWeeklyThemeFacade weeklyThemes) : ControllerBase
{
    /// <summary>Returns the workspace's weekly themes.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// The creator's own editorial week — at most one theme per day, so at most seven, and fewer is normal. These
    /// are not a platform list: a creator writes their own, nothing is seeded, and no two workspaces share a
    /// theme.
    ///
    /// The live week comes first in a creator's reading order, Monday through Sunday, followed by retired themes.
    /// A retired theme is reported rather than hidden, with `retiredAt` set, because its `key` may already be
    /// stored by other records and because a client may offer to bring it back. A workspace with no themes
    /// answers `200` with an empty list, not `404`: an empty week is where every workspace starts, and a consumer
    /// reading it simply has no theme for the day. Every member including a Viewer may read. The response is
    /// `no-store`: it is workspace-private and editable.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<WeeklyThemeWeekServiceModel>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string workspaceSlug, CancellationToken cancellationToken)
    {
        var result = await weeklyThemes.GetAsync(cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        Response.Headers.CacheControl = "no-store";

        return Ok(result.Value);
    }

    /// <summary>Replaces the workspace's weekly themes.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed; the workspace is resolved server-side (tenancy.md).
    /// </param>
    /// <param name="model">The whole week. An omitted or empty `themes` list clears every day.</param>
    /// <param name="idempotencyKey">
    /// Optional. A repeat of the same key and the same body returns the original response with
    /// `Idempotent-Replayed: true`.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Editor or above. One write for the whole week rather than per-theme CRUD: at most seven day-keyed entries
    /// make the week a single value, which keeps the write idempotent, lets a creator clear a day by leaving it
    /// out, and keeps "one theme per day" an invariant of one transaction instead of a race between endpoints.
    ///
    /// A theme is recognised by its `key`, which is stable and is what other records store. Submitting a key the
    /// workspace already has keeps that theme — its `day`, `displayName` and `description` are updated, and it is
    /// brought back if it was retired. Submitting a new key creates a theme at `revision` 1. A live theme whose
    /// key is *absent* is retired, not deleted: its row and its key survive, so a record that already named it
    /// keeps resolving, and its day is freed. A request asking for the week the workspace already has writes
    /// nothing and bumps no revision.
    ///
    /// `displayName` is the creator's own words and the server does not police them. `key` must be lowercase and
    /// match `^[a-z0-9][a-z0-9-]*$`; each `day` and each `key` may appear once. Two creators saving at the same
    /// time is last-write-wins by design — the week is one value, so there is no field-level merge to lose — and
    /// a genuine collision between two concurrent replaces answers `409 content.weekly_themes.conflict`. A
    /// workspace already holding the maximum number of rows, retired ones included, answers
    /// `422 content.weekly_themes.limit.unprocessable`; the remedy is to delete a retired theme.
    /// </remarks>
    [HttpPut]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<WeeklyThemeWeekServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Replace(
        string workspaceSlug,
        ReplaceWeeklyThemesViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await weeklyThemes.ReplaceAsync(userId, model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, Ok);
    }

    /// <summary>Deletes one weekly theme outright.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed; the workspace is resolved server-side (tenancy.md).
    /// </param>
    /// <param name="key">The theme's stable key.</param>
    /// <param name="idempotencyKey">Optional. A repeat of the same key and body returns the original response.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Owner only, and audited. This is not how a creator clears a day — leaving a theme out of `PUT` retires it,
    /// which is reversible and keeps its key resolving for anything that stored it. This deletes the row, so the
    /// key resolves to nothing afterwards and a record that stored it keeps only the creator's text. It exists for
    /// a theme written by mistake, and because the retired tail is what the per-workspace row limit counts.
    ///
    /// The response is the remaining week, exactly as a read returns it. A key this workspace does not have
    /// answers `404 content.weekly_theme.not_found` — as does another workspace's key, so one workspace's themes
    /// are never disclosed to another. Creating a theme under a deleted key again later is allowed, and it then
    /// resolves again, to whatever the creator has written under it now.
    /// </remarks>
    [HttpDelete("{key}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceOwner)]
    [ProducesResponseType<WeeklyThemeWeekServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Delete(
        string workspaceSlug,
        string key,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await weeklyThemes.DeleteAsync(userId, key, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, Ok);
    }
}
