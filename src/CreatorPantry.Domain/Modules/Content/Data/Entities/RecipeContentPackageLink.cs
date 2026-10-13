using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Content.Data.Entities;

/// <summary>
/// The exact records a card's recipe package was put together from (13.4, RCPUB-006): one recipe version, the
/// editorial revision written for it, the posts, the pictures, the channels and the window the creator wants
/// it out in. Interior to <see cref="ContentCard"/>; a card has at most one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Pins, not copies.</strong> Every field names a record the workspace already owns, by the exact
/// version. Nothing is flattened into the link, so the package can always be traced back to what it was made
/// from, and a source that has since moved is detectable by comparing the pin with the record (content.md).
/// Whether a package is blocked or stale is read from those records when asked; it is not stored here, where
/// it could go out of date.
/// </para>
/// <para>
/// <strong>Write-once.</strong> A package put together again is a new link on a new card, never an edit of
/// this one — which is what makes "the exact approved versions" a fact about the row rather than about when it
/// was last read. The rows leave only with their card.
/// </para>
/// <para>
/// <strong>The window is an intention.</strong> It is what the creator asked for, as an instant range, the
/// workspace zone it was asked in and the local times they meant (publishing.md). It schedules nothing and
/// names no provider.
/// </para>
/// </remarks>
public class RecipeContentPackageLink : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ContentCardId { get; set; }

    public Guid RecipeId { get; set; }

    /// <summary>The version of <see cref="RecipeId"/> the package is for. Always pinned.</summary>
    public Guid RecipeVersionId { get; set; }

    /// <summary>The editorial proposal for this recipe, with <see cref="EditorialRevisionId"/>; both or neither.</summary>
    public Guid? EditorialProposalId { get; set; }

    /// <summary>The revision of <see cref="EditorialProposalId"/> that was approved.</summary>
    public Guid? EditorialRevisionId { get; set; }

    /// <summary>The posts written for this work. Which revision of which channel is each channel row's to say.</summary>
    public Guid? SocialPackageId { get; set; }

    /// <summary>The start of the desired window, in UTC. The five window fields are set together or not at all.</summary>
    public DateTimeOffset? WindowStartsAt { get; set; }

    public DateTimeOffset? WindowEndsAt { get; set; }

    /// <summary>The workspace's IANA zone when the window was asked for.</summary>
    public string? WindowTimeZoneId { get; set; }

    /// <summary>The start as the creator meant it, in <see cref="WindowTimeZoneId"/>.</summary>
    public DateTime? WindowLocalStartsAt { get; set; }

    public DateTime? WindowLocalEndsAt { get; set; }

    /// <summary>
    /// When an Editor explicitly allowed this package to stand although a source it pins needs review, or null
    /// when nobody has. Set with <see cref="NeedsReviewAllowedByMembershipId"/> or not at all.
    /// </summary>
    /// <remarks>
    /// A package whose source has moved is blocked, and that is worked out from the pinned records each time it
    /// is read — it is not stored, because it would go out of date. What <em>is</em> stored is this: the one
    /// fact about a blocked package that no source record can say, that a person looked at it and let it stand.
    /// </remarks>
    public DateTimeOffset? NeedsReviewAllowedAt { get; set; }

    /// <summary>Who allowed it. Not a foreign key, as elsewhere.</summary>
    public Guid? NeedsReviewAllowedByMembershipId { get; set; }

    /// <summary>
    /// The reasons that were looked at and allowed, as <c>RecipePackageReview.Encode</c> writes them. Set with
    /// the allowance or not at all.
    /// </summary>
    /// <remarks>
    /// An allowance is of what was looked at. Without this, letting a package stand because its editorial
    /// needed review would also hide a recipe archived the week after — which nobody looked at. A reason that
    /// is not in this list blocks the package however it came to be allowed.
    /// </remarks>
    public string? NeedsReviewAllowedReasons { get; set; }

    /// <summary>Not a foreign key, as elsewhere.</summary>
    public Guid CreatedByMembershipId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<RecipeContentPackageLinkAsset> Assets { get; set; } = [];

    public List<RecipeContentPackageLinkChannel> Channels { get; set; } = [];
}
