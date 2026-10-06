using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Domain.Modules.Media.Data.Entities;

/// <summary>
/// One piece of finished creative the workspace owns and reuses: a photograph, a generated image a creator
/// kept, an imported still. The digital asset library's aggregate root (DAM-001 through DAM-010).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the media aggregate three modules have been waiting for.</strong>
/// <c>RecipeAssetLink</c>, <c>BrandAssetLink</c> and <c>TestAttachmentLink</c> each carry a
/// <c>MediaAssetId</c> that has had no foreign key since Phase 2, with their configurations saying the
/// constraint arrives with this table and naming its shape. That is why the table is
/// <c>MediaAssets</c> rather than <c>DamAssets</c>: three shipped columns and <c>tenancy.md</c> already
/// call it that, and renaming them to match a prompt's wording would be a migration across three modules
/// to change nothing. "DAM" stays the language of the facades and routes above.
/// </para>
/// <para>
/// <strong>Metadata only.</strong> The bytes are private objects named by a
/// <see cref="MediaAssetVersion"/>; nothing here holds an image, a URL, or anything a client could turn
/// into one (media.md).
/// </para>
/// <para>
/// <strong>Mutable description over immutable versions</strong>, exactly as <c>BrandSourceDocument</c>
/// does it. A creator may retitle, retag and reclassify; the bytes change only by adding a version.
/// <see cref="CurrentVersionNumber"/> is a counter rather than a foreign key, so the root and its versions
/// do not point at each other and an insert never has to be ordered around a cycle.
/// </para>
/// <para>
/// <strong>Soft deleted, never removed by ordinary code.</strong> <see cref="DeletedAt"/> and
/// <see cref="DeletedByMembershipId"/> are a tombstone a creator can be shown and an administrator can
/// account for; a status flag alone could not say who or when, which DAM-005 asks for. Links from recipes
/// and brand profiles are deliberately left standing — 12.9e decides what a linked deletion looks like,
/// and silently cutting a recipe's photograph is the one thing it must not do.
/// </para>
/// </remarks>
public class MediaAsset : IWorkspaceOwned
{
    public Guid Id { get; set; }

    /// <summary>Stamped by <c>WorkspaceOwnershipInterceptor</c> from the resolved context, never by hand.</summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>The creator's own name for it. Required, because a library nobody can scan is not a library.</summary>
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Where the bytes came from. See <see cref="MediaAssetKind"/>.</summary>
    public MediaAssetKind Kind { get; set; }

    /// <summary>
    /// What the image shows, for anyone who cannot see it.
    /// </summary>
    /// <remarks>
    /// Authored, never inferred. Nothing in this codebase may write alt text from a filename, a recipe
    /// title or a prompt: that would be describing pixels nothing has analysed, which <c>ai.md</c> and
    /// <c>media.md</c> both forbid. Null until a creator writes it or an analysis of the actual image
    /// proposes one they accept.
    /// </remarks>
    public string? AltText { get; set; }

    /// <summary>The channel this asset was made for, or null. Opaque, as everywhere else channels appear.</summary>
    public string? ChannelKey { get; set; }

    /// <summary>The platform it was made for, or null. Opaque for the same reason.</summary>
    public string? PlatformKey { get; set; }

    /// <summary>The weekly-theme day it belongs to, or null. Matches <c>WorkspaceWeeklyTheme.Day</c>.</summary>
    public DayOfWeek? Day { get; set; }

    /// <summary>The visual style it was shot or generated in, or null. Opaque workspace vocabulary.</summary>
    public string? StyleKey { get; set; }

    /// <summary>Platform reference vocabulary, shared and not workspace-owned (tenancy.md).</summary>
    public Guid? CuisineId { get; set; }

    /// <inheritdoc cref="CuisineId"/>
    public Guid? CourseId { get; set; }

    /// <summary>
    /// Who holds the rights, and how the asset must be credited. Free text.
    /// </summary>
    /// <remarks>
    /// Required of an asset by media.md, and the reason a staged generated image is not one: provenance is
    /// what a staged image has, and rights are what a creator decides when they keep it.
    /// </remarks>
    public string? RightsHolder { get; set; }

    /// <inheritdoc cref="RightsHolder"/>
    public string? AttributionText { get; set; }

    /// <summary>The newest version's number, starting at 1: an asset is created with its first version.</summary>
    public int CurrentVersionNumber { get; set; } = 1;

    /// <summary>When the asset was soft-deleted, or null while it is live.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <inheritdoc cref="CreatedByMembershipId"/>
    public Guid? DeletedByMembershipId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Not a foreign key, matching every other root here: authorship outlives membership.</summary>
    public Guid CreatedByMembershipId { get; set; }

    /// <inheritdoc cref="CreatedByMembershipId"/>
    public Guid UpdatedByMembershipId { get; set; }

    /// <summary>The optimistic concurrency token DAM-004's patch checks.</summary>
    public byte[] RowVersion { get; set; } = [];

    public List<MediaAssetVersion> Versions { get; set; } = [];

    public List<MediaAssetTag> Tags { get; set; } = [];
}
