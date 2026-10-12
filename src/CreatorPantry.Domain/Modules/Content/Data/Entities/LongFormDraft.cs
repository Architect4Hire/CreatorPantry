using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Data.Entities;

/// <summary>
/// One long-form piece — a blog article or a newsletter issue — for one creative context, and where it stands
/// (LF.1.2, baseline B-32). Workspace-owned, and the root of the long-form aggregate.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A slot, not a body</strong> — the division <see cref="ContentProposal"/> draws. It holds the mutable
/// state: <see cref="Status"/>, which revision the creator accepted, and whether that acceptance has gone
/// stale. The words and the source pins live on the immutable <see cref="LongFormRevision"/> rows under it.
/// </para>
/// <para>
/// <strong>Rooted on a context, not a recipe.</strong> A piece need not be about a recipe at all, and a creator
/// writes more than one newsletter about the same one, so a context may hold any number of drafts of either
/// kind. Nothing here holds a recipe fact and nothing writes back to <c>Recipe</c>, <c>RecipeVersion</c> or
/// <see cref="ContentProposal"/>.
/// </para>
/// <para>
/// <strong>No "current revision" column.</strong> The latest revision is the highest <c>RevisionNumber</c>;
/// <see cref="AcceptedRevisionId"/> is a decision rather than a derivation.
/// </para>
/// <para>
/// <strong>Not a publication.</strong> No provider id, no schedule and no delivery state: those belong to
/// <c>Publication</c> records.
/// </para>
/// </remarks>
public class LongFormDraft : IWorkspaceOwned
{
    public Guid Id { get; set; }

    /// <summary>Stamped by <c>WorkspaceOwnershipInterceptor</c> from the resolved context, never by hand.</summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>The piece of work this is written for.</summary>
    public Guid CreativeContextId { get; set; }

    public LongFormKind Kind { get; set; }

    public LongFormDraftStatus Status { get; set; }

    /// <summary>
    /// The revision the creator last accepted as the article of record, retained through every later move.
    /// Null until the first acceptance. Required while <see cref="Status"/> is Accepted or NeedsReview.
    /// </summary>
    public Guid? AcceptedRevisionId { get; set; }

    /// <summary>
    /// When the creator accepted <see cref="AcceptedRevisionId"/>; present exactly when it is.
    /// </summary>
    public DateTimeOffset? AcceptedAt { get; set; }

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
