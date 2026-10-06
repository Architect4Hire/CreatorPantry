using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// What a repository read of one asset found: the root's own columns, its counts, and its concurrency token.
/// </summary>
/// <remarks>
/// <para>
/// Projected in SQL, so no entity is materialised and <strong>no object key is ever loaded into a layer that
/// could return one</strong> — the version rows come separately and leave it out too. The same argument
/// <c>BrandSourceDocumentDetailRecord</c> makes, and the reason it is a projection rather than a tracked read.
/// </para>
/// <para>
/// The three counts are computed in the same statement because all three are facts a detail panel shows and none
/// is worth a round trip: a version count, how many times the asset has been used, and how many recipes link to
/// it. <see cref="RecipeLinkCount"/> counts links the asset holds, which is not the same as links whose recipe
/// can be named — see <see cref="MediaAssetDetailServiceModel.RecipeLinks"/>.
/// </para>
/// </remarks>
public sealed record MediaAssetDetailRecord(
    Guid Id,
    string Title,
    string? Description,
    string? AltText,
    MediaAssetKind Kind,
    string? ChannelKey,
    string? PlatformKey,
    DayOfWeek? Day,
    string? StyleKey,
    Guid? CuisineId,
    Guid? CourseId,
    string? RightsHolder,
    string? AttributionText,
    int CurrentVersionNumber,
    int VersionCount,
    int UtilizationCount,
    int RecipeLinkCount,
    DateTimeOffset? DeletedAt,
    Guid? DeletedByMembershipId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    byte[] RowVersion);

/// <summary>One version of an asset, as the detail read projects it. Never the object key.</summary>
public sealed record MediaAssetVersionRecord(
    int VersionNumber,
    string MediaType,
    int Width,
    int Height,
    long SizeBytes,
    string ContentChecksum,
    string? OriginalFileName,
    MediaAssetVersionSource Source,
    Guid? SourceGeneratedImageId,
    DateTimeOffset CreatedAt);

/// <summary>One tag on an asset, with the name resolved from the workspace's own vocabulary.</summary>
public sealed record MediaAssetTagRecord(Guid Id, string Name);

/// <summary>
/// One recipe link an asset holds, before the recipe is named.
/// </summary>
/// <remarks>
/// The title is absent on purpose: <c>Recipe</c> is the Recipes module's entity and no foreign key runs from an
/// asset to a recipe, so this module may not read it. The name is resolved a layer up through
/// <c>IRecipeFacade.ListTitlesAsync</c>.
/// </remarks>
public sealed record MediaAssetRecipeLinkRecord(Guid RecipeId, RecipeAssetRole Role, string? Caption);

/// <summary>Everything one detail read found, in one bundle, so the layers above compose rather than query.</summary>
public sealed record MediaAssetDetailBundle(
    MediaAssetDetailRecord Asset,
    IReadOnlyList<MediaAssetVersionRecord> Versions,
    IReadOnlyList<MediaAssetTagRecord> Tags,
    IReadOnlyList<MediaAssetRecipeLinkRecord> RecipeLinks);
