using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// One version's facts, as a detail read publishes them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Never <c>ObjectKey</c>, and never a URL.</strong> The bytes are reached through the render and
/// download routes, which stream them; there is no address for a client to hold (media.md).
/// </para>
/// <para>
/// <see cref="ContentChecksum"/> <em>is</em> published, following
/// <c>BrandSourceDocumentVersionDetailServiceModel</c>: it is what a download's <c>ETag</c> is built from, so a
/// client holding both can tell whether the bytes it has are still the bytes this version names. A verification
/// aid, not an address — nothing can be fetched with it. It stays off the search row, where on a hundred cards
/// it would be noise.
/// </para>
/// </remarks>
/// <param name="MediaType">Established by reading the bytes, never from what a client declared.</param>
/// <param name="OriginalFileName">
/// The creator's own filename, for display only. <strong>Not</strong> what a download is named, and never what
/// an object key is built from.
/// </param>
public sealed record MediaAssetVersionServiceModel(
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

/// <summary>A tag on an asset, named rather than left as an id.</summary>
public sealed record MediaAssetTagServiceModel(Guid Id, string Name);

/// <summary>
/// One recipe this asset is used by.
/// </summary>
/// <remarks>
/// The title is resolved through <see cref="Recipes.Facade.IRecipeFacade"/>, so a link whose recipe the
/// workspace cannot see is absent rather than named — which is why a client must tolerate fewer links here than
/// the asset's own <c>RecipeLinkCount</c> reports.
/// </remarks>
public sealed record MediaAssetRecipeLinkServiceModel(
    Guid RecipeId,
    string Title,
    RecipeAssetRole Role,
    string? Caption);

/// <summary>One logged use of an asset (DAM-009), as a history page publishes it.</summary>
/// <param name="UtilizedOn">
/// A calendar date in the workspace's own zone, not an instant — "which day did this go out" is a date
/// question, and storing it as one is what keeps it stable across a timezone change.
/// </param>
/// <param name="UtilizedDay">
/// The day of the week <paramref name="UtilizedOn"/> fell on, derived when the row was written rather than here:
/// re-deriving it would use this server's calendar rather than the workspace's zone.
/// </param>
public sealed record MediaAssetUtilizationServiceModel(
    Guid Id,
    string PlatformKey,
    DateOnly UtilizedOn,
    DayOfWeek UtilizedDay,
    string? CampaignName,
    string? Notes,
    DateTimeOffset CreatedAt);

/// <summary>
/// One asset as a client reads it on its own (DAM-003): everything the creator said about it, its current
/// version's facts, its version history, its lineage, and the token the next change must quote.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No object key, no container, no URL, and no bytes</strong> — here or anywhere a client can reach
/// (media.md). The image is fetched through the render route (12.9f) and the download routes (12.9g, 12.9h).
/// </para>
/// <para>
/// <strong>Versions inline, utilization paged.</strong> An asset has a handful of versions and 12.9h downloads
/// one by number, so the whole list belongs here. Utilization gains a row every time the asset is used, so it
/// grows without limit over years and is read through its own cursor-paged route; only its count is here.
/// </para>
/// <para>
/// <strong>Lineage is resolved, not left as ids.</strong> A panel saying "used in recipe 3f2a…" is useless to a
/// creator, so recipe titles come through <c>IRecipeFacade.ListTitlesAsync</c> and prompt labels through
/// <c>IPromptRecordFacade.ListForAssetAsync</c>.
/// </para>
/// <para>
/// <see cref="RecipeLinks"/> and <see cref="RecipeLinkCount"/> agree, because <c>RecipeAssetLink</c> carries
/// <c>(WorkspaceId, RecipeId)</c> to <c>(WorkspaceId, Id)</c> on <c>Recipe</c> and cascades on delete: a link
/// pointing at a recipe this workspace cannot name is refused by the database, not merely unexpected. The layer
/// that composes this drops a link it cannot name rather than inventing a placeholder title, which is a guard
/// against a future shape that key does not cover — not a divergence anything can reach today.
/// </para>
/// <para>
/// <strong>Soft-deleted assets answer 404 unless the caller asked for one</strong>, which is the policy approved
/// for this prompt. <see cref="DeletedAt"/> and <see cref="DeletedByMembershipId"/> are the tombstone DAM-005
/// needs to report who deleted an asset and when; they are null for a live asset and are the only reason this
/// read can be made to return a deleted one at all. Every byte route refuses regardless.
/// </para>
/// <para>
/// <see cref="DeletedByMembershipId"/> is the <em>only</em> membership id published here, and that asymmetry is
/// deliberate: who uploaded an asset or last edited it is nobody's business on a read, while who deleted one is
/// the question DAM-005 exists to answer and no other route could. It is a membership id rather than a name, so
/// naming the person is a separate lookup a caller makes deliberately.
/// </para>
/// </remarks>
/// <param name="RecipeLinkCount">
/// How many recipe links the asset holds. Equal to <c>RecipeLinks.Count</c> — see the lineage note above for why
/// the schema guarantees that rather than merely expecting it.
/// </param>
/// <param name="BrandProfileCount">
/// How many brand profiles use the asset, and <paramref name="TestAttachmentCount"/> how many recipe test-run
/// attachments do. Counted as <see cref="MediaAssetAffectedContentServiceModel"/> counts them, so a client can
/// warn about every link a removal would leave standing <em>before</em> removing, rather than learning of two of
/// the three kinds only from the removal's own response. Counts and not names, for the reason that model gives.
/// Added by 12.10h.
/// </param>
/// <param name="ConcurrencyToken">
/// Opaque. Stored and sent back on the next change (12.9d); never parsed, compared or ordered by.
/// </param>
public sealed record MediaAssetDetailServiceModel(
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
    IReadOnlyList<MediaAssetTagServiceModel> Tags,
    MediaAssetVersionServiceModel? CurrentVersion,
    IReadOnlyList<MediaAssetVersionServiceModel> Versions,
    int VersionCount,
    int UtilizationCount,
    int RecipeLinkCount,
    int BrandProfileCount,
    int TestAttachmentCount,
    IReadOnlyList<MediaAssetRecipeLinkServiceModel> RecipeLinks,
    IReadOnlyList<AssetPromptServiceModel> Prompts,
    DateTimeOffset? DeletedAt,
    Guid? DeletedByMembershipId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ConcurrencyToken);
