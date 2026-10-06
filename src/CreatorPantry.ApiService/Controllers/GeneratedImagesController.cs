using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Facade;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>
/// The staged images one generation produced: looking at them, keeping a copy, and declining one
/// (IMG-005, IMG-006).
/// </summary>
/// <remarks>
/// <para>
/// <strong>An image is named by its id and never by its location.</strong> No route here takes a path, a
/// key or a filename, so there is nothing a traversal sequence could be put in — the storage key is
/// generated from the resolved workspace, the operation and the variant, and never leaves the domain
/// (media.md). No URL to a staging object is ever issued.
/// </para>
/// <para>
/// An unknown image, another workspace's, and one whose bytes retention has already removed all answer
/// `404 media.staged_image.not_found`, so none of them tells a caller about the others.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/generated-images")]
public sealed class GeneratedImagesController(IStagedImageFacade images) : ControllerBase
{
    /// <summary>Renders one staged image for viewing in the browser.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="generatedImageId">The image to render. Constrained to a Guid.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may read. Identical to the download below but for the disposition: this is `inline`, so a
    /// browser shows it where a page asked for it, and that is the only difference. `Content-Type` is the
    /// media type **established from the returned bytes at staging**, never one a provider declared.
    /// `X-Content-Type-Options: nosniff` and `Cache-Control: no-store` are set, and the response carries a
    /// strong `ETag` — the stored content checksum — so `If-None-Match` answers 304. Staged bytes are
    /// immutable, so that tag identifies this representation for as long as it exists.
    /// Metadata that exists whose bytes cannot be read answers `503 media.staged_image.unavailable`
    /// rather than 404: the image is there, and retrying is the remedy. No range requests.
    /// </remarks>
    [HttpGet("{generatedImageId:guid}/preview")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public Task<IActionResult> Preview(
        string workspaceSlug, Guid generatedImageId, CancellationToken cancellationToken) =>
        SendAsync(generatedImageId, inline: true, cancellationToken);

    /// <summary>Downloads one staged image, byte for byte as the provider returned it.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="generatedImageId">The image to download. Constrained to a Guid.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may read. `Content-Disposition: attachment` names it `generated-{n}.{ext}`, where `n` is
    /// the variant counted from one and the extension comes from the media type established from the bytes
    /// — so the name describes what is actually there. The name is ASCII `a-z0-9`, one hyphen and one dot
    /// by construction and carries no id, workspace, prompt or storage location. Everything else matches
    /// the preview above, including the entity tag and the 503 for unreadable bytes.
    /// </remarks>
    [HttpGet("{generatedImageId:guid}/content")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public Task<IActionResult> Download(
        string workspaceSlug, Guid generatedImageId, CancellationToken cancellationToken) =>
        SendAsync(generatedImageId, inline: false, cancellationToken);

    /// <summary>Declines one staged image, so retention may remove it.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="generatedImageId">The image to decline. Constrained to a Guid.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above — the same role that may ask for images in the first place. The row moves to
    /// `Rejected` and the bytes stay until the retention sweep collects them, which is what makes this
    /// answer fast and makes a half-done delete impossible: there is no second system to fail.
    /// `204` whether this call declined the image or a previous one already had, so a client that retries
    /// gets the answer it wanted rather than a conflict. A kept or expired image answers
    /// `409 media.staged_image.conflict`; an unknown or another workspace's answers
    /// `404 media.staged_image.not_found`.
    /// **A declined image is not a DAM asset and never becomes one** (IMG-006).
    /// </remarks>
    [HttpDelete("{generatedImageId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Reject(
        string workspaceSlug, Guid generatedImageId, CancellationToken cancellationToken)
    {
        var result = await images.RejectAsync(generatedImageId, cancellationToken);

        return result.Succeeded ? NoContent() : this.ProblemFor(result.Error!);
    }

    /// <summary>
    /// The one place that turns an opened staged image into a response.
    /// </summary>
    /// <remarks>
    /// Preview and download differ by one header, so they share everything else rather than drifting: a
    /// change to the caching, the entity tag or the sniffing protection that reached only one of them
    /// would be a hole in the other.
    /// </remarks>
    private async Task<IActionResult> SendAsync(
        Guid generatedImageId, bool inline, CancellationToken cancellationToken)
    {
        var result = await images.OpenAsync(generatedImageId, cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        var download = result.Value!;

        // First, before anything that could throw. The facade hands back an *open* object, so from here to
        // the end of the request something must own releasing it. The lease rather than the stream:
        // disposing the lease is what the store's contract says releases a read, and it is registered on
        // the 304 path too so neither branch has a dispose of its own to forget.
        Response.RegisterForDisposeAsync(download);

        // Strong, and legitimately so: staged bytes are never rewritten — the store is create-only and a
        // variant has one row — so the checksum identifies this representation for as long as it exists.
        var entityTag = new EntityTagHeaderValue($"\"{download.ContentChecksum}\"");

        Response.Headers.CacheControl = "no-store";
        Response.Headers.ETag = entityTag.ToString();

        if (Request.GetTypedHeaders().IfNoneMatch is { Count: > 0 } candidates
            && candidates.Any(candidate => candidate.Compare(entityTag, useStrongComparison: true)))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        // Private creator material: nothing may be sniffed into another type. EdgeHardening already puts
        // `default-src 'none'` on every response from this host, which is stricter than anything this
        // route would add, so no Content-Security-Policy is set here.
        Response.Headers.XContentTypeOptions = "nosniff";

        // ASCII a-z, 0-9, one hyphen and one dot by construction, so there is nothing to quote or encode.
        // Set here rather than through FileStreamResult's FileDownloadName so that one piece of code
        // decides the name and the header it lands in.
        Response.Headers.ContentDisposition =
            new ContentDispositionHeaderValue(inline ? "inline" : "attachment")
            {
                FileName = download.FileName,
            }.ToString();

        // The stored size, so a client can show progress. Safe to state because staging refuses to commit
        // a row whose size storage disagrees with: the row's size is the object's size.
        Response.ContentLength = download.SizeBytes;

        // `enableRangeProcessing` is left off and no EntityTag is set on the result: this contract offers
        // no ranges, and the conditional request is decided above, so exactly one place compares a tag.
        return new FileStreamResult(download.Content, download.MediaType);
    }
}
