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
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/prompts")]
public sealed class PromptsController(IPromptRecordFacade prompts) : ControllerBase
{
    /// <summary>Lists the prompts in the workspace's library, newest first.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="query">
    /// The filters, cursor and page size. Carries no workspace — see <see cref="PromptSearchViewModel"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer may read. Cursor-paged: follow `nextCursor` until it is null rather than
    /// comparing counts against a page size the server may have clamped. A cursor is bound to the workspace and
    /// filters it was issued for, so changing either means starting again without one —
    /// `content.prompt.cursor.invalid` says so explicitly. `limit` is clamped rather than refused. `totalCount`
    /// counts every match across all pages and is present unless `includeTotal=false`; it is a row count, not a
    /// page count, because a keyset ordering has no page N to jump to.
    ///
    /// **Newest first is the only ordering**, so there is no `sort` parameter. Ties between prompts saved in the
    /// same instant are broken by id, which is what keeps a page boundary from repeating one and skipping the
    /// other.
    ///
    /// **Each row carries a preview, not the prompt.** `textPreview` is truncated server-side and `textLength`
    /// is the full length, so a client can show an ellipsis without fetching what it is eliding; the whole
    /// prompt, its model draft and its template provenance come from the detail route. An empty library and
    /// filters matching nothing are both an empty page, never a `404`, and a `channel` naming no channel at all
    /// is the same — a read keeps answering for keys the catalogue has retired, because a prompt that stored one
    /// is still in the library. The response is `no-store`: it is workspace-private creator content.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<PromptSearchPageServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> List(
        string workspaceSlug,
        [FromQuery] PromptSearchViewModel query,
        CancellationToken cancellationToken)
    {
        // Set before the result is examined, so a refusal carries it too. A ProblemDetails body here holds only
        // field messages, but "which header this response got" should not depend on whether it succeeded.
        Response.Headers.CacheControl = "no-store";

        var result = await prompts.SearchAsync(query, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Reads one prompt of the workspace named by the route, in full.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="promptRecordId">
    /// The prompt to read. Constrained to a Guid, so a malformed id never reaches this action: routing answers
    /// 404, the same status as an unknown prompt and as one belonging to another workspace. The problem body
    /// differs — an edge 404 carries the generic <c>not_found</c> code rather than this module's — so nothing is
    /// disclosed, but a client branching on <c>code</c> sees two codes for what is one condition to it.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer may read. **This is the only route that publishes the prompt itself**:
    /// the list carries a truncated `textPreview` and a `textLength`, and the whole `text`, the model's
    /// `generatedText` and the `promptTemplate*` triple are here. The response is `no-store` — it is
    /// workspace-private creator content, and it is the creator's craft.
    ///
    /// An unknown id answers `404 content.prompt.not_found`. **Another workspace's prompt answers exactly that
    /// — the same code, the same message, the same body** — so a prompt id cannot be used to ask what a
    /// neighbour owns. That is not two branches written to match: the workspace query filter means the server
    /// never sees the other row at all.
    ///
    /// **A prompt cannot say who saved it.** The row records its author as a membership id, and membership ids
    /// never leave the server; no route can turn one into a person yet either. An author becomes a compatible
    /// addition once a workspace-members endpoint exists. For the same shape of reason there is no
    /// `generatedImageId` and no `damAssetId`: the columns exist, nothing can write them until 12.6 and 12.9,
    /// and a field that is always null would claim a lineage this server cannot record.
    /// </remarks>
    [HttpGet("{promptRecordId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<PromptDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(
        string workspaceSlug,
        Guid promptRecordId,
        CancellationToken cancellationToken)
    {
        // Set before the result is examined, so a refusal carries it too — "which header this response got"
        // should not depend on whether the prompt was found.
        Response.Headers.CacheControl = "no-store";

        var result = await prompts.GetDetailAsync(promptRecordId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Saves a prompt to the workspace's library.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="model">
    /// The prompt and its lineage. Carries no workspace, author, id, or asset reference — see
    /// <see cref="SavePromptRecordViewModel"/>.
    /// </param>
    /// <param name="idempotencyKey">
    /// Optional. A repeat of the same key and the same prompt returns the original response with
    /// `Idempotent-Replayed: true` rather than saving a second copy.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor or above, the same bar as creating a recipe. A saved prompt is **immutable**: it is the
    /// evidence of what produced an image that may already be published, so there is no update and no delete —
    /// not even of `label`. A reworked prompt is a new record.
    ///
    /// `text` is authoritative: send the prompt as it was actually used, after any edit the creator made. When a
    /// model wrote the draft, send it as `generatedText` beside it and name the proposal and the template that
    /// produced it; the two are kept side by side so the library can still answer what the creator changed.
    /// `source` decides which of those are required — `Manual` refuses all of them, every other value requires
    /// `aiProposalId`, `generatedText` and all three `promptTemplate*` fields. A prompt may pin a recipe, and
    /// with it the exact version it was written against; a version without its recipe is refused, and so is a
    /// version of a different recipe.
    ///
    /// `channelKey` must name a channel CreatorPantry still writes for; retired channels are refused as a new
    /// choice. A pin naming a recipe, version or proposal this workspace does not have answers
    /// `422 content.prompt.lineage.unprocessable` — as does another workspace's id, in the same words, so one
    /// workspace's content is never disclosed to another. A malformed prompt answers
    /// `400 content.prompt.invalid` with field errors.
    ///
    /// **There is no `generatedImageId` or `damAssetId` yet.** Those columns exist on the record but have no
    /// foreign key until 12.6 and 12.9, so this server cannot verify an id a client sends for them — and the
    /// row is immutable, so a wrong value could never be corrected. Each field arrives with the release that
    /// can check it. Until then a saved prompt carries no asset lineage, and a client should not synthesise one.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<SavedPromptRecordServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Save(
        string workspaceSlug,
        SavePromptRecordViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await prompts.SaveAsync(userId, model, idempotencyKey, cancellationToken);

        // The location is the only thing this layer contributes, because only it knows the route shape. It names
        // the detail route above, which now serves it — `A_created_prompt_is_readable_at_its_location_header`
        // fetches the header rather than trusting that the two strings still agree.
        return this.IdempotentResult(outcome, saved =>
            Created($"/api/v1/workspaces/{workspaceSlug}/prompts/{saved.PromptRecordId}", saved));
    }
}
