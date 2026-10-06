namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>The editorial metadata a creator supplies when an asset is created (DAM-001).</summary>
/// <remarks>
/// <strong>No workspace, no object key, no dimensions.</strong> The workspace comes from the resolved
/// context; the rest are facts about the bytes that the server establishes by reading them, and a request
/// that could state them could state them wrongly.
/// </remarks>
public sealed record MediaAssetMetadataInput
{
    public string? Title { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// What the image shows, for anyone who cannot see it.
    /// </summary>
    /// <remarks>
    /// Only ever the creator's own words. Nothing in this codebase writes alt text from a filename, a
    /// recipe title or a prompt — that would be describing pixels nothing has analysed (ai.md, media.md).
    /// </remarks>
    public string? AltText { get; init; }

    public string? ChannelKey { get; init; }

    public string? PlatformKey { get; init; }

    public DayOfWeek? Day { get; init; }

    public string? StyleKey { get; init; }

    public Guid? CuisineId { get; init; }

    public Guid? CourseId { get; init; }

    public string? RightsHolder { get; init; }

    public string? AttributionText { get; init; }

    /// <summary>Tags from the workspace's own vocabulary. Unknown ids are refused, never created here.</summary>
    public IReadOnlyList<Guid>? WorkspaceTagIds { get; init; }
}

/// <summary>
/// The recipe this asset is for, when it is for one.
/// </summary>
/// <remarks>
/// Written as a <c>RecipeAssetLink</c>, the link the recipe library has always used — 12.9 established that
/// this is the recipe lineage rather than a DAM-owned table of its own.
/// </remarks>
public sealed record MediaAssetRecipeLinkInput(Guid RecipeId, string? Caption);

/// <summary>One asset as the application reports it after creation.</summary>
/// <remarks>
/// <strong>No object key and no URL</strong>, here or anywhere a client can reach (media.md). A caller that
/// wants the bytes asks for them by id, which is 12.9f and 12.9g.
/// </remarks>
public sealed record MediaAssetServiceModel(
    Guid Id,
    string Title,
    MediaAssetKind Kind,
    int CurrentVersionNumber,
    string MediaType,
    int Width,
    int Height,
    long SizeBytes,
    Guid? SourceGeneratedImageId,
    Guid? PromptRecordId,
    Guid? RecipeId,
    DateTimeOffset CreatedAt);

/// <summary>A creator's upload, with the request's metadata and lineage beside it.</summary>
/// <param name="Content">Opened for reading. The caller owns disposing it.</param>
/// <param name="FileName">The creator's own filename, kept for display only. Never used to build a key.</param>
/// <param name="UserId">The authenticated caller, for the idempotency scope. Never request input.</param>
/// <remarks>
/// There is no membership id here, deliberately: authorship comes from <c>IWorkspaceContext</c> where the
/// write happens, so a caller cannot pass somebody else's by mistake.
/// </remarks>
public sealed record MediaAssetUpload(
    Stream Content,
    string? FileName,
    MediaAssetMetadataInput Metadata,
    MediaAssetRecipeLinkInput? RecipeLink,
    Content.Managers.PromptRecordSaveInput? Prompt,
    string UserId);

/// <inheritdoc cref="MediaAssetUpload"/>
/// <param name="GeneratedImageId">The staged image to keep. Resolved in the caller's own workspace.</param>
public sealed record MediaAssetFromGeneratedImage(
    Guid GeneratedImageId,
    MediaAssetMetadataInput Metadata,
    MediaAssetRecipeLinkInput? RecipeLink,
    Content.Managers.PromptRecordSaveInput? Prompt,
    string UserId);
