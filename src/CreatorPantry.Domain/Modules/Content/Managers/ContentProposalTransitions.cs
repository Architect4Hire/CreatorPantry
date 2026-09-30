using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// One legal move between proposal states, and what it takes to make it.
/// </summary>
/// <param name="From">The state the proposal is in; <c>null</c> for creation, which is also a recorded move.</param>
/// <param name="MinimumRole">
/// The least workspace role that may make the move, or <c>null</c> when only the system may: a creator
/// cannot declare their own content stale or fresh by hand.
/// </param>
/// <param name="WritesRevision">Whether the move appends a <c>ContentRevision</c>.</param>
/// <param name="SetsAcceptedRevision">Whether the move points the proposal at an accepted revision.</param>
public sealed record ContentProposalTransitionRule(
    ContentProposalStatus? From,
    ContentProposalStatus To,
    WorkspaceRole? MinimumRole,
    bool WritesRevision = false,
    bool SetsAcceptedRevision = false);

/// <summary>
/// Every move a content proposal may make (RCPUB-002), in one place a reviewer can read. A move not listed
/// does not exist: <see cref="Find"/> answers <c>null</c> and the caller refuses.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Accepted content is never lost by a move.</strong> Nothing here clears the accepted revision
/// pointer, so a rejection, a regeneration or a staleness mark leaves the creator's last accepted words
/// intact. Only a newer acceptance replaces the <em>pointer</em>; the old revision stays in history.
/// </para>
/// <para>
/// <strong>Staleness is applied from <see cref="ContentProposalStatus.Accepted"/> only.</strong> An unaccepted
/// proposal has no claim of currency to withdraw; acceptance of a revision whose pins no longer match the
/// sources is a rule for whichever seam writes the acceptance.
/// </para>
/// <para>Versioned as a whole, recorded on every transition, so a stored history stays readable after the machine changes.</para>
/// </remarks>
public static class ContentProposalTransitions
{
    public const string Version = "1.0.0";

    public static readonly IReadOnlyList<ContentProposalTransitionRule> All =
    [
        // Creation: a first revision exists and awaits a decision.
        new(null, ContentProposalStatus.Proposed, WorkspaceRole.Contributor, WritesRevision: true),

        // A new revision (regeneration or creator edit) from any resting state. Contributor: drafting is the work.
        new(ContentProposalStatus.Proposed, ContentProposalStatus.Proposed, WorkspaceRole.Contributor, WritesRevision: true),
        new(ContentProposalStatus.Accepted, ContentProposalStatus.Proposed, WorkspaceRole.Contributor, WritesRevision: true),
        new(ContentProposalStatus.NeedsReview, ContentProposalStatus.Proposed, WorkspaceRole.Contributor, WritesRevision: true),
        new(ContentProposalStatus.Rejected, ContentProposalStatus.Proposed, WorkspaceRole.Contributor, WritesRevision: true),

        // Decisions. Editor, because accepting is what lets content move toward publication.
        new(ContentProposalStatus.Proposed, ContentProposalStatus.Accepted, WorkspaceRole.Editor, SetsAcceptedRevision: true),
        new(ContentProposalStatus.Proposed, ContentProposalStatus.Rejected, WorkspaceRole.Editor),

        // Reaffirm: stale content confirmed as still standing, as a new re-pinned revision.
        new(ContentProposalStatus.NeedsReview, ContentProposalStatus.Accepted, WorkspaceRole.Editor, WritesRevision: true, SetsAcceptedRevision: true),

        // Staleness. System only.
        new(ContentProposalStatus.Accepted, ContentProposalStatus.NeedsReview, null),
    ];

    /// <summary>The rule for one move, or <c>null</c> when there is no such move.</summary>
    public static ContentProposalTransitionRule? Find(ContentProposalStatus? from, ContentProposalStatus to) =>
        All.FirstOrDefault(rule => rule.From == from && rule.To == to);

    /// <summary>Every state a proposal in this one can move to.</summary>
    public static IReadOnlyList<ContentProposalStatus> From(ContentProposalStatus? status) =>
        [.. All.Where(rule => rule.From == status).Select(rule => rule.To)];
}
