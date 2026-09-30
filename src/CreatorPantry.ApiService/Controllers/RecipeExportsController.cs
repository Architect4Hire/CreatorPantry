using System.Security.Cryptography;
using System.Text;
using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/recipes/{recipeId:guid}/exports")]
public sealed class RecipeExportsController(IRecipeExportFacade exports) : ControllerBase
{
    public const string JsonLdContentType = "application/ld+json";

    public const string MarkdownContentType = "text/markdown";

    public const string WarningsHeader = "Cp-Export-Warnings";

    /// <summary>Generates schema.org Recipe JSON-LD for one approved version of a recipe.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="recipeId">
    /// The recipe to export. Constrained to a Guid, so a malformed id answers 404 at routing — the same status
    /// as an unknown recipe and as one belonging to another workspace.
    /// </param>
    /// <param name="query">Which version, and which accepted SEO revision. Carries no workspace and no recipe.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Read-only (RCPUB-002): a projection of the creator's own stored fields, never a stored document, and
    /// nothing in it is inferred — no nutrition, rating, dietary claim or time the creator did not record. The
    /// body is the JSON-LD and nothing else, with `Content-Type: application/ld+json`, so it can be embedded
    /// as it is. Omitting `versionNumber` exports the recipe's current version; it is never replaced by an
    /// older approved one. Only an approved version, or one marked ready, is exported: any other answers
    /// `409 recipes.jsonLdExport.notApproved.conflict`. There is no draft override.
    /// The description and keywords come from the accepted SEO revision only when it was accepted for this
    /// exact version and has not since been marked for review; otherwise they are left out and the warning
    /// `recipe_json_ld_editorial_not_current` is listed in the `Cp-Export-Warnings` header, a comma-separated
    /// list of stable codes that is absent when there is nothing to report. A `seoRevision` that is not the
    /// accepted revision answers `404 content.revision.not_found`; a `versionNumber` the recipe lacks answers
    /// `404 recipes.version.not_found`, naming the parameter.
    /// Structured data needs an image, and no authorized image URL is available until media is built, so until
    /// then this answers `422 recipes.jsonLdExport.incomplete.unprocessable` whose `missingRequired` lists
    /// each fact the document cannot be published without — it never invents one. The response is private
    /// and must be revalidated: `ETag` and `If-None-Match` are supported and a match answers 304.
    /// </remarks>
    [HttpGet("json-ld")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<string>(StatusCodes.Status200OK, JsonLdContentType)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> GetJsonLd(
        string workspaceSlug,
        Guid recipeId,
        [FromQuery] RecipeJsonLdExportViewModel query,
        CancellationToken cancellationToken)
    {
        var result = await exports.GetJsonLdAsync(recipeId, query, cancellationToken);
        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        var export = result.Value!;

        if (ApplyValidators(export.Json, export.Warnings.Select(warning => warning.Code)))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Content(export.Json, new MediaTypeHeaderValue(JsonLdContentType) { Encoding = Encoding.UTF8 }.ToString());
    }

    /// <summary>Renders one approved version of a recipe as a Markdown file.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="recipeId">
    /// The recipe to export. Constrained to a Guid, so a malformed id answers 404 at routing — the same status
    /// as an unknown recipe and as one belonging to another workspace.
    /// </param>
    /// <param name="query">Which version, template, unit presentation and accepted editorial revision. Carries no workspace and no recipe.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Read-only (RCPUB-003): a projection of the creator's own stored fields, never a stored document, with
    /// every creator string escaped so none can become Markdown structure, HTML or a link. The body is the
    /// Markdown and nothing else, with `Content-Type: text/markdown; charset=utf-8`, and is offered as a
    /// download: `Content-Disposition: attachment` with a deterministic ASCII name, the recipe's title as a
    /// slug plus `-v{version}.md`, that carries no id, workspace or path. Omitting `versionNumber` exports the
    /// recipe's current version; it is never replaced by an older approved one. Only an approved version, or
    /// one marked ready, is exported: any other answers `409 recipes.markdownExport.notApproved.conflict`.
    /// `units` appends a converted quantity to a line — `(≈ 237 ml)` — computed by the canonical conversion
    /// code and never replacing the creator's words; a line that cannot be converted stays as written and is
    /// listed in `Cp-Export-Warnings`, a comma-separated list of stable codes that is absent when there is
    /// nothing to report. The standard template includes the accepted editorial revision only when it was
    /// accepted for this exact version and has not since been marked for review; the compact one includes no
    /// editorial copy and ignores `editorialRevision`. An `editorialRevision` that is not the accepted
    /// revision answers `404 content.revision.not_found`; a `versionNumber` the recipe lacks answers
    /// `404 recipes.version.not_found`, naming the parameter. A missing title, ingredient or instruction
    /// answers `422 recipes.markdownExport.incomplete.unprocessable` whose `missingRequired` lists each one.
    /// The response is private and must be revalidated: `ETag` and `If-None-Match` are supported and a match
    /// answers 304.
    /// </remarks>
    [HttpGet("markdown")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<string>(StatusCodes.Status200OK, MarkdownContentType)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> GetMarkdown(
        string workspaceSlug,
        Guid recipeId,
        [FromQuery] RecipeMarkdownExportViewModel query,
        CancellationToken cancellationToken)
    {
        var result = await exports.GetMarkdownAsync(recipeId, query, cancellationToken);
        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        var export = result.Value!;

        // The name is ASCII a-z, 0-9 and hyphens by construction, so a bare filename is safe in the
        // header with nothing to quote or encode; it is never built from a path, an id or the workspace.
        Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileName = export.FileName,
        }.ToString();
        Response.Headers.XContentTypeOptions = "nosniff";

        if (ApplyValidators(export.Markdown, export.Warnings.Select(warning => warning.Code)))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Content(export.Markdown, new MediaTypeHeaderValue(MarkdownContentType) { Encoding = Encoding.UTF8 }.ToString());
    }

    /// <summary>
    /// Sets the headers every export carries and answers whether the caller's validator already matches.
    /// </summary>
    /// <returns><c>true</c> when the request's <c>If-None-Match</c> matches, so the body must not be sent.</returns>
    private bool ApplyValidators(string body, IEnumerable<string> warningCodes)
    {
        var warnings = string.Join(", ", warningCodes);

        // A personalized response: a shared proxy must not keep it, and a client must ask again before
        // reusing it, so a changed recipe or accepted revision is never served from an earlier answer.
        Response.Headers.CacheControl = "private, no-cache";
        Response.Headers.Vary = HeaderNames.Cookie;
        if (warnings.Length > 0)
        {
            Response.Headers[WarningsHeader] = warnings;
        }

        // Over the warnings as well as the body: the same content can be produced with and without a warning.
        var etag = new EntityTagHeaderValue(
            "\"" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body + "\n" + warnings)))[..32] + "\"");
        Response.Headers.ETag = etag.ToString();

        return Request.GetTypedHeaders().IfNoneMatch.Any(candidate =>
            candidate.Equals(EntityTagHeaderValue.Any) || candidate.Compare(etag, useStrongComparison: false));
    }
}
