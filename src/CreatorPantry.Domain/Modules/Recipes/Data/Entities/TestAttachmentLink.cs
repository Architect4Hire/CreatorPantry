using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Entities;

/// <summary>
/// One use of a media asset by a test run — the photograph of the collapsed loaf. Interior to the
/// <see cref="RecipeTestRun"/> aggregate: it records the usage, never the asset.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A link, not a copy.</strong> There is no byte column here and no duplicated metadata: the asset is
/// independently owned creator property with its own storage, its own alt text and its own retention rules
/// (media.md), and a test run that duplicated any of it would own a second version that goes stale silently.
/// <see cref="Caption"/> is the exception for the reason <see cref="RecipeAssetLink.Caption"/> gives — a
/// caption is written for this usage.
/// </para>
/// <para>
/// <see cref="MediaAssetId"/> deliberately has <strong>no foreign key</strong>, exactly as
/// <see cref="RecipeAssetLink.MediaAssetId"/> does not: the asset aggregate arrives with the media library
/// and its own migration adds the constraint. Two things follow, and both are easy to get wrong later.
/// </para>
/// <para>
/// The constraint that eventually lands must be composite —
/// <c>(WorkspaceId, MediaAssetId) -> MediaAssets (WorkspaceId, Id)</c> — and never on
/// <c>MediaAssetId</c> alone. This link's own workspace says nothing about the asset's, so a single-column
/// key would happily attach another workspace's photograph to this test.
/// </para>
/// <para>
/// Until then this is a client-supplied identifier reaching storage unchecked, and the write seam must
/// validate it against the resolved workspace through the media facade before persisting. "Attachments link
/// authorized assets" is a promise nothing below the seam can keep on its own.
/// </para>
/// </remarks>
public class TestAttachmentLink : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid RecipeTestRunId { get; set; }

    /// <summary>
    /// The issue this is evidence for, when it is evidence for one. Null for a photograph of the run as a
    /// whole.
    /// </summary>
    public Guid? TestIssueId { get; set; }

    /// <summary>The linked asset. See the type's remarks for what does and does not constrain it.</summary>
    public Guid MediaAssetId { get; set; }

    /// <summary>Position among this run's attachments, unique within the run.</summary>
    public int SortOrder { get; set; }

    /// <summary>The caption written for this test. Not alt text, which belongs to the asset.</summary>
    public string? Caption { get; set; }
}
