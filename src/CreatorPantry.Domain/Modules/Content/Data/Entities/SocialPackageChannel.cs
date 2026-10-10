using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Data.Entities;

/// <summary>
/// One channel's output in a <see cref="SocialPackage"/>, and where it stands in the creator's review. Interior
/// to the package.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A slot, not a body</strong> — the same division <see cref="ContentProposal"/> draws. It holds the
/// mutable state, and every alternative, regeneration and edit is a <see cref="SocialRevision"/> under it.
/// This row is what makes each channel accepted or rejected on its own: a decision about one channel writes
/// one slot and no other.
/// </para>
/// <para>
/// <see cref="ChannelKey"/> is a <c>ContentChannel.Key</c> and opaque here. The write seam validates it against
/// the catalogue when the slot is created and never again, so a key that is later retired keeps its slot and
/// goes on taking revisions and decisions.
/// </para>
/// <para>
/// <strong>No "current revision" column.</strong> The latest revision is the highest <c>RevisionNumber</c>;
/// <see cref="AcceptedRevisionId"/> is a decision rather than a derivation, and no move clears it.
/// </para>
/// </remarks>
public class SocialPackageChannel : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid SocialPackageId { get; set; }

    public string ChannelKey { get; set; } = string.Empty;

    /// <summary>The same states and moves as a content proposal; see <see cref="ContentProposalTransitions"/>.</summary>
    public ContentProposalStatus Status { get; set; }

    /// <summary>
    /// The revision the creator last accepted for this channel, retained through every later move. Required
    /// while <see cref="Status"/> is Accepted or NeedsReview.
    /// </summary>
    public Guid? AcceptedRevisionId { get; set; }

    /// <summary>When the accepted revision was found out of step with its recipe; null unless NeedsReview.</summary>
    public DateTimeOffset? StaleSince { get; set; }

    /// <summary>Which pinned sources moved; <see cref="ContentStaleReasons.None"/> unless NeedsReview.</summary>
    public ContentStaleReasons StaleReasons { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>What refuses a decision composed against a slot somebody else has since moved.</summary>
    public byte[] RowVersion { get; set; } = [];
}
