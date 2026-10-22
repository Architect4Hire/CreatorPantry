using System.Security.Cryptography;
using System.Text;
using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using CreatorPantry.ApiService.RateLimiting;
using CreatorPantry.Domain.Managers.Limits;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>
/// LF.5.2: an accepted long-form draft as a file — Markdown or HTML — for its creator to take elsewhere.
/// </summary>
/// <remarks>
/// Its own controller, as the recipe exports have theirs: a file with download headers is a different kind of
/// answer from the JSON the draft routes give. Each action calls the one facade once.
/// </remarks>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/long-form-drafts/{draftId:guid}/exports")]
public sealed class LongFormExportsController(ILongFormExportFacade exports) : ControllerBase
{
    public const string MarkdownContentType = "text/markdown";

    public const string HtmlContentType = "text/html";

    public const string WarningsHeader = "Cp-Export-Warnings";

    /// <summary>Renders an accepted long-form draft as a Markdown file.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="draftId">
    /// The draft to export. Constrained to a Guid, so a malformed id answers 404 at routing — the same status
    /// as an unknown draft and as one belonging to another workspace.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Read-only (LFC-007): a projection of the revision the creator accepted, never a stored document, and
    /// the same accepted revision always gives the same bytes. There is nothing to choose and so no query
    /// parameter: the document is the accepted revision's — a title when the piece has one, an issue's note,
    /// the written sections in plan order under the creator's own headings, and an attribution naming the
    /// revision and the recipe versions it was written from. The title, headings and recipe titles are each
    /// escaped to one inert line; a section's text is the creator's own Markdown and is written as it stands.
    ///
    /// The body is the Markdown and nothing else, with `Content-Type: text/markdown; charset=utf-8`, offered
    /// as a download: `Content-Disposition: attachment` with a deterministic ASCII name — the title as a slug
    /// (or `article` / `newsletter` when there is none) plus `-r{revision}.md` — that carries no id, workspace
    /// or path.
    ///
    /// Only an accepted draft is exported, and there is no override: any other answers
    /// `409 content.long_form.export.not_accepted.conflict`. A draft accepted and since marked for review is
    /// still its creator's accepted article and is exported as it was accepted, with
    /// `content.long_form.export.sources_moved` in `Cp-Export-Warnings` — a comma-separated list of stable
    /// codes, absent when there is nothing to report, which may also carry
    /// `content.long_form.export.sections_unwritten` and `content.long_form.export.recipe_unavailable`. An
    /// accepted revision with no written section answers
    /// `422 content.long_form.export.nothing_written.unprocessable`.
    ///
    /// The response is private and must be revalidated: `ETag` and `If-None-Match` are supported and a match
    /// answers 304. A refusal is never stored.
    /// </remarks>
    [OperationLimit(LimitedOperation.TextExport)]
    [HttpGet("markdown")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<string>(StatusCodes.Status200OK, MarkdownContentType)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public Task<IActionResult> GetMarkdown(string workspaceSlug, Guid draftId, CancellationToken cancellationToken) =>
        ExportAsync(draftId, LongFormExportFormat.Markdown, MarkdownContentType, cancellationToken);

    /// <summary>Renders an accepted long-form draft as an HTML file.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="draftId">
    /// The draft to export. Constrained to a Guid, so a malformed id answers 404 at routing — the same status
    /// as an unknown draft and as one belonging to another workspace.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// The same document as the Markdown export, with the same rules for what is exported, the same warnings
    /// and the same refusals, as one complete unstyled HTML page. Every creator string is escaped; HTML a
    /// creator typed into their Markdown is written as its text and never as markup; a link is kept only when
    /// it is `http`, `https` or `mailto`, and a picture only when it is `http` or `https`. Nothing is fetched
    /// and no bytes are embedded.
    ///
    /// The body is the page and nothing else, with `Content-Type: text/html; charset=utf-8`, and is always a
    /// download, never shown in place: `Content-Disposition: attachment` with the same name as the Markdown
    /// export ending `.html`. Like every response from this API it carries the edge's
    /// `Content-Security-Policy: default-src 'none'; …`, so a browser that renders it from this origin
    /// regardless runs, loads and submits nothing.
    ///
    /// The response is private and must be revalidated: `ETag` and `If-None-Match` are supported and a match
    /// answers 304. A refusal is never stored.
    /// </remarks>
    [OperationLimit(LimitedOperation.TextExport)]
    [HttpGet("html")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<string>(StatusCodes.Status200OK, HtmlContentType)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public Task<IActionResult> GetHtml(string workspaceSlug, Guid draftId, CancellationToken cancellationToken) =>
        ExportAsync(draftId, LongFormExportFormat.Html, HtmlContentType, cancellationToken);

    private async Task<IActionResult> ExportAsync(
        Guid draftId, LongFormExportFormat format, string contentType, CancellationToken cancellationToken)
    {
        var result = await exports.ExportAsync(draftId, format, cancellationToken);

        if (!result.Succeeded)
        {
            return ExportProblem(result.Error!);
        }

        var export = result.Value!;

        // The name is ASCII a-z, 0-9 and hyphens by construction, so a bare filename is safe in the
        // header with nothing to quote or encode; it is never built from a path, an id or the workspace.
        Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileName = export.FileName,
        }.ToString();
        Response.Headers.XContentTypeOptions = "nosniff";

        if (ApplyValidators(export.Content, export.Warnings.Select(warning => warning.Code)))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Content(export.Content, new MediaTypeHeaderValue(contentType) { Encoding = Encoding.UTF8 }.ToString());
    }

    /// <summary>
    /// A refusal, marked so nothing keeps it: a 409 describes this draft as it stands now, so a shared cache or
    /// a stale client copy must never replay it once the draft is accepted.
    /// </summary>
    private ObjectResult ExportProblem(OperationError error)
    {
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.Vary = HeaderNames.Cookie;

        return this.ProblemFor(error);
    }

    /// <summary>
    /// Sets the headers every export carries and answers whether the caller's validator already matches.
    /// </summary>
    /// <returns><c>true</c> when the request's <c>If-None-Match</c> matches, so the body must not be sent.</returns>
    private bool ApplyValidators(string body, IEnumerable<string> warningCodes)
    {
        var warnings = string.Join(", ", warningCodes);

        // A personalized response: a shared proxy must not keep it, and a client must ask again before
        // reusing it, so a draft accepted afresh is never served from an earlier answer.
        Response.Headers.CacheControl = "private, no-cache";
        Response.Headers.Vary = HeaderNames.Cookie;

        if (warnings.Length > 0)
        {
            Response.Headers[WarningsHeader] = warnings;
        }

        // Over the warnings as well as the content: the same content can be produced with and without a warning.
        var etag = new EntityTagHeaderValue(
            "\"" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body + "\n" + warnings)))[..32] + "\"");
        Response.Headers.ETag = etag.ToString();

        return Request.GetTypedHeaders().IfNoneMatch.Any(candidate =>
            candidate.Equals(EntityTagHeaderValue.Any) || candidate.Compare(etag, useStrongComparison: false));
    }
}
