using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>
/// Adding finished creative to the workspace's library (DAM-001).
/// </summary>
/// <remarks>
/// <para>
/// Two routes because there are two sources of bytes and they are validated differently: an upload is a
/// stranger's file and is inspected and scanned, while a staged image was both when the worker staged it.
/// They are not two kinds of asset — both commit through one transaction.
/// </para>
/// <para>
/// <strong>Neither response carries an address.</strong> An asset's bytes are read by id through 12.9f
/// and 12.9g; no URL for a DAM object is issued to anyone (media.md).
/// </para>
/// </remarks>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/dam-assets")]
public sealed class DamAssetsController(IMediaAssetFacade assets) : ControllerBase
{
    /// <summary>Adds an uploaded image to the library as a new asset.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="form">The file and its metadata. Carries no workspace, owner or object identity.</param>
    /// <param name="idempotencyKey">
    /// Optional, and worth sending. Without it a client whose upload committed but whose response was lost
    /// retries and creates a <em>second</em> asset — there is no natural key to prevent that, because a
    /// creator may legitimately upload the same photograph twice as two assets.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above. The media type is established from the file's bytes, never from the declared
    /// content type or the filename, and the file is scanned before anything stores it — a file whose bytes
    /// are not a supported image answers `422 media.asset.unprocessable` whatever it was called. The
    /// creator's filename is kept for display only and never becomes part of a key or a header.
    /// `201 Created` carries the asset; storage that could not be reached answers
    /// `503 media.asset.unavailable` with nothing written.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MediaPolicy.AssetUploadRequestMaxBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MediaPolicy.AssetUploadRequestMaxBytes)]
    [ProducesResponseType<MediaAssetServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> Upload(
        string workspaceSlug,
        [FromForm] CreateDamAssetForm form,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (form.File is null)
        {
            return this.ProblemFor(Domain.Managers.Results.OperationError.Validation(
                MediaErrorCodes.AssetInvalidRequest,
                "The asset could not be created.",
                [("file", "A file is required.")]));
        }

        // The form reader has already buffered the part, so this stream is seekable: it is read to
        // inspect, again to scan and again to store.
        await using var content = form.File.OpenReadStream();

        var outcome = await assets.CreateFromUploadAsync(
            new MediaAssetUpload(
                content,
                form.File.FileName,
                form.ToMetadata(),
                form.ToRecipeLink(),
                Prompt: null,
                User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value),
            idempotencyKey,
            cancellationToken);

        return Respond(outcome);
    }

    /// <summary>Keeps a staged generated image as a library asset.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="model">The image to keep, its metadata, and the prompt that produced it.</param>
    /// <param name="idempotencyKey">Optional, and largely belt-and-braces here — see the remarks.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above. The bytes are copied into the library and the staged image becomes `Kept`,
    /// which is what lets retention collect the staging copy: nothing is removed until a committed asset
    /// owns one.
    ///
    /// **Naturally idempotent, with or without a key.** An image is kept exactly once, so a repeat returns
    /// the asset the first call made rather than creating a second or failing. An unknown image, another
    /// workspace's, and one already rejected or expired all answer `404 media.asset.not_found`.
    ///
    /// A prompt sent with the request is written **inside the same transaction** as the asset, so the two
    /// commit together or not at all — a prompt record cannot be deleted afterwards, so it must never
    /// commit beside an asset that did not. A refused prompt takes the asset down with it and answers with
    /// the prompt's own error.
    /// </remarks>
    [HttpPost("from-generated-image")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<MediaAssetServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> KeepGeneratedImage(
        string workspaceSlug,
        [FromBody] KeepGeneratedImageViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var outcome = await assets.CreateFromGeneratedImageAsync(
            new MediaAssetFromGeneratedImage(
                model.GeneratedImageId ?? Guid.Empty,
                model.Metadata ?? new MediaAssetMetadataInput(),
                model.RecipeLink,
                model.Prompt,
                User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value),
            idempotencyKey,
            cancellationToken);

        return Respond(outcome);
    }

    /// <summary>
    /// One shape for both routes: the created asset, or the refusal that stopped it.
    /// </summary>
    /// <remarks>
    /// A replay answers 201 with the first call's asset rather than 200, because the client's request did
    /// result in exactly this asset existing — which is what the key promised it would mean.
    /// </remarks>
    private IActionResult Respond(IdempotentOutcome<MediaAssetServiceModel> outcome) =>
        outcome.Result.Succeeded
            ? StatusCode(StatusCodes.Status201Created, outcome.Result.Value)
            : this.ProblemFor(outcome.Result.Error!);
}

/// <summary>The multipart body of an upload. Every field is optional here and validated in the domain.</summary>
/// <remarks>
/// Bound loosely on purpose, exactly as <c>UploadBrandSourceDocumentForm</c> is: a missing or malformed
/// form field should answer with this feature's own validation error rather than with the model binder's,
/// which cannot say which of a creator's fields was the problem.
/// </remarks>
public sealed class CreateDamAssetForm
{
    [FromForm(Name = "file")]
    public IFormFile? File { get; set; }

    [FromForm(Name = "title")]
    public string? Title { get; set; }

    [FromForm(Name = "description")]
    public string? Description { get; set; }

    [FromForm(Name = "altText")]
    public string? AltText { get; set; }

    [FromForm(Name = "channelKey")]
    public string? ChannelKey { get; set; }

    [FromForm(Name = "platformKey")]
    public string? PlatformKey { get; set; }

    [FromForm(Name = "day")]
    public DayOfWeek? Day { get; set; }

    [FromForm(Name = "styleKey")]
    public string? StyleKey { get; set; }

    [FromForm(Name = "cuisineId")]
    public Guid? CuisineId { get; set; }

    [FromForm(Name = "courseId")]
    public Guid? CourseId { get; set; }

    [FromForm(Name = "rightsHolder")]
    public string? RightsHolder { get; set; }

    [FromForm(Name = "attributionText")]
    public string? AttributionText { get; set; }

    [FromForm(Name = "tagIds")]
    public List<Guid>? TagIds { get; set; }

    [FromForm(Name = "recipeId")]
    public Guid? RecipeId { get; set; }

    [FromForm(Name = "caption")]
    public string? Caption { get; set; }

    internal MediaAssetMetadataInput ToMetadata() => new()
    {
        Title = Title,
        Description = Description,
        AltText = AltText,
        ChannelKey = ChannelKey,
        PlatformKey = PlatformKey,
        Day = Day,
        StyleKey = StyleKey,
        CuisineId = CuisineId,
        CourseId = CourseId,
        RightsHolder = RightsHolder,
        AttributionText = AttributionText,
        WorkspaceTagIds = TagIds,
    };

    internal MediaAssetRecipeLinkInput? ToRecipeLink() =>
        RecipeId is { } recipeId ? new MediaAssetRecipeLinkInput(recipeId, Caption) : null;
}

/// <summary>The body of a keep request.</summary>
/// <remarks>
/// <strong>No asset id, no object key and no media metadata.</strong> The asset is being created, the key
/// is the server's to generate, and the media facts come from the staged image's own row — a request that
/// could state them could state them wrongly.
/// </remarks>
public sealed record KeepGeneratedImageViewModel
{
    public Guid? GeneratedImageId { get; init; }

    public MediaAssetMetadataInput? Metadata { get; init; }

    public MediaAssetRecipeLinkInput? RecipeLink { get; init; }

    /// <summary>
    /// The prompt that produced the image, saved inside the same transaction as the asset.
    /// </summary>
    /// <remarks>
    /// It carries no asset id of its own: the asset does not exist when the request is written, and the
    /// server fills it in from inside the transaction that commits both.
    /// </remarks>
    public PromptRecordSaveInput? Prompt { get; init; }
}
