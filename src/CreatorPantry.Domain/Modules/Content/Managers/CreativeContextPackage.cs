using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>Why something a creative context names is not in a package.</summary>
public enum CreativeContextDropReason
{
    /// <summary>
    /// It did not resolve in this workspace when the package was assembled.
    /// </summary>
    /// <remarks>
    /// One reason for a source that no longer exists, one that is archived, deleted, declined or expired, and
    /// one that was never this workspace's. Telling them apart here would let a package be used to ask what a
    /// neighbour holds — and a generation has no use for the difference.
    /// </remarks>
    Unavailable = 1,

    /// <summary>The task this package was assembled for does not ground in this kind of source.</summary>
    NotUsedByTask = 2,

    /// <summary>The package already carries as many sources of this kind as it may.</summary>
    OverCap = 3,

    /// <summary>It resolved, and the package had no room left for it.</summary>
    OverBudget = 4,
}

/// <summary>A part of a source that was left out whole because it was too long to carry.</summary>
public enum CreativeContextOmission
{
    /// <summary>The context names a theme key that is not a theme of this workspace.</summary>
    ThemeUnavailable = 1,

    ThemeDescriptionOverCap = 2,

    ConceptSummaryOverCap = 3,

    /// <summary>Alt text too long to carry; the picture is reported as not described rather than half described.</summary>
    AltTextOverCap = 4,

    RecipeIngredientsOverCap = 5,

    RecipeStepsOverCap = 6,
}

/// <summary>Where a picture's description came from.</summary>
public enum CreativeContextPictureDescriptionSource
{
    /// <summary>Nobody has described this picture. It contributes only the fact that it is attached.</summary>
    NotDescribed = 0,

    /// <summary>The alt text a creator wrote, or accepted, on the library asset.</summary>
    CreatorAltText = 1,

    // 2 is reserved for a stored analysis of the picture's own pixels, which arrives with AF.3.4. Until then
    // nothing in this codebase stores one a picture can be looked up by.
}

/// <summary>The creator's own words for the piece. Untrusted prompt content, like everything a creator types.</summary>
public sealed record CreativeContextWords(string? WorkingTitle, string? PictureBrief);

/// <summary>One channel the piece is for. Platform data: the key and the catalogue's own name for it.</summary>
public sealed record CreativeContextChannelEntry(string Key, string DisplayName);

/// <summary>
/// The day the piece is for and its theme, in the creator's words.
/// </summary>
/// <param name="ThemeRevision">The theme's edit counter when it was read, so a later reader can tell it has moved.</param>
public sealed record CreativeContextDayEntry(
    DayOfWeek? Day,
    string? ThemeKey,
    string? ThemeName,
    string? ThemeDescription,
    int? ThemeRevision);

/// <summary>
/// Recipe facts, read from one exact immutable version.
/// </summary>
/// <remarks>
/// The creator's entered lines, verbatim and in order — never a normalised quantity, never a re-derived line
/// (recipes.md). The list stops whole at the first line that does not fit, and what follows is counted, so a
/// reader knows the list is short — and short at the end, never with a line missing from the middle.
/// </remarks>
public sealed record CreativeContextRecipeEntry(
    Guid ReferenceId,
    Guid RecipeId,
    Guid RecipeVersionId,
    int VersionNumber,
    string Title,
    string? YieldText,
    int? PrepTimeMinutes,
    int? CookTimeMinutes,
    int? RestTimeMinutes,
    int? TotalTimeMinutes,
    IReadOnlyList<string> Ingredients,
    IReadOnlyList<string> Steps,
    int OmittedIngredientCount,
    int OmittedStepCount);

/// <summary>A chosen recipe concept. Text a model wrote earlier and a creator picked; untrusted like the rest.</summary>
public sealed record CreativeContextConceptEntry(
    Guid ReferenceId,
    Guid ConceptRequestId,
    Guid ConceptId,
    string Title,
    string? Summary);

/// <summary>
/// One picture, and only what somebody has said about it.
/// </summary>
/// <param name="Kind"><c>DamAsset</c> or <c>GeneratedImage</c>.</param>
/// <param name="PictureId">The asset id or the generated image id, by <paramref name="Kind"/>.</param>
/// <param name="VersionNumber">The asset version the description was read beside; null for a generated image.</param>
/// <param name="Description">
/// Null when <paramref name="DescriptionSource"/> is <c>NotDescribed</c>. Never invented from a file name, a
/// title, a recipe or a prompt (ai.md, media.md).
/// </param>
public sealed record CreativeContextPictureEntry(
    Guid ReferenceId,
    CreativeContextReferenceKind Kind,
    Guid PictureId,
    int? VersionNumber,
    CreativeContextPictureDescriptionSource DescriptionSource,
    string? Description);

/// <summary>A saved prompt's authoritative text. The record is immutable, so its id is its version.</summary>
public sealed record CreativeContextPromptEntry(Guid ReferenceId, Guid PromptRecordId, string Text);

/// <summary>Something the context names that this package does not carry, and why.</summary>
public sealed record CreativeContextDrop(Guid ReferenceId, CreativeContextReferenceKind Kind, CreativeContextDropReason Reason);

/// <summary>
/// What one generation task may be told about one piece of creative work (AF.1.5, baseline B-31).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deterministic and bounded.</strong> Assembled by code from records the caller's workspace may
/// already read, with no model in the loop. Every section has a cap, nothing is cut mid-item, and whatever was
/// left out is named in <see cref="Dropped"/> or <see cref="Omissions"/> rather than silently missing.
/// </para>
/// <para>
/// <strong>Every entry says exactly what it was read from.</strong> A recipe entry carries its version id, a
/// picture its asset version, a theme its revision, and <see cref="ContextVersion"/> is the context's own
/// concurrency token at assembly. Together with <see cref="Checksum"/> that is what lets a stored generation
/// say which words it was grounded on.
/// </para>
/// <para>
/// <strong>Nothing here is an instruction.</strong> The package holds text, keys, ids, counts and timestamps
/// and nothing that switches a rule, a warning or a tool permission on or off —
/// <c>CreativeContextPackageTests</c> holds that. Everything a creator wrote or a model produced earlier
/// is untrusted prompt content, and it is the renderer and the envelope that fence it.
/// </para>
/// <para>
/// <strong><see cref="WorkspaceId"/> is the workspace the context row itself belongs to</strong>, read with
/// it rather than supplied by a caller. It is what the renderer stamps onto the prompt segments, so an
/// envelope being built for any other workspace refuses the package — a check that would mean nothing if the
/// caller supplied both sides of it. It is never rendered.
/// </para>
/// <para>
/// A task that grounds in nothing gets an empty package: no words, no sources, no context version.
/// </para>
/// </remarks>
public sealed record CreativeContextPackage(
    Guid WorkspaceId,
    AiTaskType TaskType,
    Guid ContextId,
    string? ContextVersion,
    CreativeContextWords? Words,
    IReadOnlyList<CreativeContextChannelEntry> Channels,
    CreativeContextDayEntry? Day,
    IReadOnlyList<CreativeContextRecipeEntry> Recipes,
    IReadOnlyList<CreativeContextConceptEntry> Concepts,
    IReadOnlyList<CreativeContextPictureEntry> Pictures,
    IReadOnlyList<CreativeContextPromptEntry> Prompts,
    IReadOnlyList<CreativeContextDrop> Dropped,
    IReadOnlyList<CreativeContextOmission> Omissions,
    int EstimatedTokens,
    string Checksum,
    DateTimeOffset AssembledAt)
{
    /// <summary>
    /// All a picture contributes when nobody has described it. Fixed wording, so a model is told a picture
    /// exists without being handed anything it could mistake for a description of one.
    /// </summary>
    public const string UndescribedPicture = "A picture is attached, not described.";
}
