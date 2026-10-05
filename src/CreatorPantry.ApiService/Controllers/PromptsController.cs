using System.Text;
using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.Net.Http.Headers;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/prompts")]
public sealed class PromptsController(IPromptRecordFacade prompts) : ControllerBase
{
    /// <summary>The media type of PRM-004's download, without its charset.</summary>
    public const string PlainTextContentType = "text/plain";

    /// <summary>The media type of PRM-005's export document, without its charset.</summary>
    public const string JsonContentType = "application/json";

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
    /// addition once a workspace-members endpoint exists. For a related reason this response carries no
    /// `generatedImageId` and no `damAssetId`. Nothing can write `damAssetId` until 12.9. `generatedImageId` is
    /// writable from 12.6, but an id is only worth publishing to a client that can fetch the image, and the
    /// authorized retrieval that resolves one arrives with 12.8 — a compatible addition when it does.
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

    /// <summary>Downloads one prompt of the workspace named by the route as a plain-text file.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="promptRecordId">
    /// The prompt to download. Constrained to a Guid, so a malformed id never reaches this action: routing
    /// answers 404 with the edge's generic <c>not_found</c> code rather than this module's, exactly as it does
    /// for the detail route above.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer may read, the same bar as the detail route — this is that read, offered
    /// as a file. **The body is the prompt and nothing else**: no header lines, no label, no channel, no
    /// template triple, no timestamp, and not even a trailing newline, so it is byte for byte the `text` the
    /// detail route publishes. A prompt's provenance travels as PRM-005's JSON record, not as commentary in a
    /// `.txt` file.
    ///
    /// `Content-Type: text/plain; charset=utf-8` with no byte-order mark, and
    /// `Content-Disposition: attachment` naming it `{label-slug}-{yyyyMMdd-HHmmss}.txt` in UTC. The name is
    /// ASCII `a-z0-9`, hyphens and one dot by construction, so there is nothing to quote or encode and no way
    /// for a label to put a path separator, a quote or a line break in the header; it carries no id, no
    /// workspace and no storage location. The timestamp is the moment the prompt was saved, which is what tells
    /// two downloads apart when their labels are the same or absent — an immutable row, so the same prompt
    /// always downloads under the same name. `X-Content-Type-Options: nosniff` is set, because a prompt is
    /// creator text that may well contain markup.
    ///
    /// The response is `no-store` and carries no `ETag`: it is workspace-private creator content, and a file
    /// save has no revalidating client to offer one to. An unknown id, another workspace's prompt and a
    /// non-member's request all answer exactly as they do on the detail route — `404
    /// content.prompt.not_found`, the same code, sentence and body, with no `Content-Disposition` on a refusal.
    /// </remarks>
    [HttpGet("{promptRecordId:guid}/text")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<string>(StatusCodes.Status200OK, PlainTextContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> GetText(
        string workspaceSlug,
        Guid promptRecordId,
        CancellationToken cancellationToken)
    {
        // Set before the result is examined, so a refusal carries it too.
        Response.Headers.CacheControl = "no-store";

        var result = await prompts.GetTextDownloadAsync(promptRecordId, cancellationToken);
        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        var download = result.Value!;

        // ASCII a-z, 0-9, hyphens and one dot by construction, so there is nothing to quote or encode — the
        // same guarantee the recipe and brand-source downloads rely on, from the same shared folding. Set here
        // rather than through a FileDownloadName so that one piece of code decides the name and the header.
        Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileName = download.FileName,
        }.ToString();

        // Private creator text, which may itself contain markup: nothing may be sniffed into another type.
        Response.Headers.XContentTypeOptions = "nosniff";

        // The prompt, verbatim. ContentResult encodes with the charset named here and writes no preamble, so
        // the body is the text's UTF-8 bytes and nothing is prepended to them.
        return Content(
            download.Text,
            new MediaTypeHeaderValue(PlainTextContentType) { Encoding = Encoding.UTF8 }.ToString());
    }

    /// <summary>Downloads one prompt of the workspace named by the route as a JSON export document.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="promptRecordId">
    /// The prompt to export. Constrained to a Guid, so a malformed id never reaches this action: routing
    /// answers 404 with the edge's generic <c>not_found</c> code rather than this module's, as on the two
    /// routes above.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer may read, the same bar as the detail route. The body is a versioned
    /// document — `{ "schemaVersion": "prompt.record.v1", "prompt": { … } }` — carrying **exactly the fields
    /// the detail route publishes**, no more: the prompt, the model's draft, the label, the channel and kind,
    /// the recipe and proposal pins, the template triple and the saved timestamp. It is written field by field
    /// rather than serialized from a type, so a field added to a response cannot arrive in a creator's
    /// archived file without a deliberate change here.
    ///
    /// **It is deterministic**: the same prompt yields the same bytes forever. Two-space indentation, `\n` line
    /// endings whatever the host, enum values as the declared names this API already publishes, `createdAt`
    /// normalized to UTC at full stored precision, nulls written rather than omitted — so "pins no template"
    /// and "predates templates" cannot read alike — and **no `exportedAt`**, which is what a clock in the
    /// document would cost. `schemaVersion` changes if a field is removed or changes meaning, never when one is
    /// added.
    ///
    /// **It carries no workspace id, no membership id, no credential, no URL and no object path.** The first
    /// two never leave the server; the rest a prompt record does not hold, so nothing here is filtering a
    /// secret out — there is none on the row to begin with. `generatedImageId` and `damAssetId` are absent for
    /// the reason the detail route gives: `damAssetId` has nothing to write it until 12.9, and
    /// `generatedImageId` waits for the retrieval that makes an id useful (12.8). Adding one later does not
    /// change `schemaVersion`.
    ///
    /// `Content-Type: application/json; charset=utf-8` with no byte-order mark, and
    /// `Content-Disposition: attachment` naming it `{label-slug}-{yyyyMMdd-HHmmss}.json` — the same name
    /// PRM-004 offers with a different extension, ASCII by construction and carrying no id, workspace or
    /// storage location. `X-Content-Type-Options: nosniff` is set. The response is `no-store` with no `ETag`,
    /// and an unknown id, another workspace's prompt and a non-member's request answer exactly as they do on
    /// the other two routes — `404 content.prompt.not_found`, with no `Content-Disposition` on a refusal.
    /// </remarks>
    [HttpGet("{promptRecordId:guid}/record")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<string>(StatusCodes.Status200OK, JsonContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> GetRecord(
        string workspaceSlug,
        Guid promptRecordId,
        CancellationToken cancellationToken)
    {
        // Set before the result is examined, so a refusal carries it too.
        Response.Headers.CacheControl = "no-store";

        var result = await prompts.GetRecordDownloadAsync(promptRecordId, cancellationToken);
        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        var export = result.Value!;

        // ASCII a-z, 0-9, hyphens and one dot by construction, as for the text download.
        Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileName = export.FileName,
        }.ToString();

        Response.Headers.XContentTypeOptions = "nosniff";

        // Content rather than Ok: the document is already written, and handing it to the response serializer
        // would re-encode it through MVC's options — which is exactly the indentation, ordering and timestamp
        // format the domain just decided, decided again somewhere else.
        return Content(
            export.Json,
            new MediaTypeHeaderValue(JsonContentType) { Encoding = Encoding.UTF8 }.ToString());
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
    /// **`generatedImageId` may be sent; `damAssetId` still may not.** The image pin is resolved through the
    /// Media module inside this workspace before anything is written, and the composite foreign key refuses a
    /// value that got past that — another workspace's image answers
    /// `422 content.prompt.lineage.unprocessable` in the same words as an id that does not exist. There is no
    /// `damAssetId` until 12.9 gives it a foreign key: this server could not verify one, and the row is
    /// immutable, so a wrong value could never be corrected. A client should not synthesise asset lineage.
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
