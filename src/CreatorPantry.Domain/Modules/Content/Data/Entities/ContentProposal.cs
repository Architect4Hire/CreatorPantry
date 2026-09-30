using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Data.Entities;

/// <summary>
/// One derivative package (editorial or SEO) for one recipe, and where it stands in the creator's review.
/// Workspace-owned, and the root of the content aggregate.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A slot, not a body.</strong> There is one per recipe and <see cref="Kind"/>; every alternative,
/// regeneration and edit is a <see cref="ContentRevision"/> under it. It holds the mutable state —
/// <see cref="Status"/>, which revision the creator accepted, and whether that acceptance has gone stale —
/// and no content and no source pins, which live on the immutable revisions they describe.
/// </para>
/// <para>
/// <strong>A derivative, never canonical.</strong> Nothing here or on a revision writes back to
/// <c>Recipe</c>. Accepting a revision accepts the copy as a derivative; the recipe's facts are unchanged.
/// </para>
/// <para>
/// <strong>No "current revision" column.</strong> The latest revision is the highest
/// <c>RevisionNumber</c>. A stored pointer would be a second statement of it that could disagree, and would
/// make creating the first revision a circular write. <see cref="AcceptedRevisionId"/> is different: it is a
/// decision, not a derivation.
/// </para>
/// <para>
/// <see cref="Status"/> and the <see cref="ContentProposalTransition"/> history are written in one transaction
/// by the write seam, so they cannot disagree.
/// </para>
/// </remarks>
public class ContentProposal : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary>The canonical recipe this derives from. A reference, never a copy of its facts.</summary>
    public Guid RecipeId { get; set; }

    public ContentPackageKind Kind { get; set; }

    public ContentProposalStatus Status { get; set; }

    /// <summary>
    /// The revision the creator last accepted, retained through every later move. Null until the first
    /// acceptance. Required while <see cref="Status"/> is Accepted or NeedsReview.
    /// </summary>
    public Guid? AcceptedRevisionId { get; set; }

    /// <summary>When the accepted revision was found to be out of step with its sources; null unless NeedsReview.</summary>
    public DateTimeOffset? StaleSince { get; set; }

    /// <summary>Which pinned sources moved; <see cref="ContentStaleReasons.None"/> unless NeedsReview.</summary>
    public ContentStaleReasons StaleReasons { get; set; }

    /// <summary>Not a foreign key: authorship outlives membership, as elsewhere.</summary>
    public Guid CreatedByMembershipId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
