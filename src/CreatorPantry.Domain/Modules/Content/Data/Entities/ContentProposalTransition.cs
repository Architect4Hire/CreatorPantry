using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Data.Entities;

/// <summary>
/// One move a content proposal made: from where, to where, by whom, when, and on which revision.
/// </summary>
/// <remarks>
/// Immutable, for the reason <c>RecipeStatusTransition</c> gives: a history that can be rewritten is not one.
/// <see cref="ActorMembershipId"/> is null when the system made the move (staleness), and
/// <see cref="Reason"/> is the creator's own words, stored as domain content and never assembled into a log line.
/// </remarks>
public class ContentProposalTransition : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ContentProposalId { get; set; }

    /// <summary>The state left; null for the creation of the proposal.</summary>
    public ContentProposalStatus? FromStatus { get; set; }

    public ContentProposalStatus ToStatus { get; set; }

    /// <summary>The revision the move concerns: the one written, accepted or rejected. Null for a staleness mark.</summary>
    public Guid? ContentRevisionId { get; set; }

    public string? Reason { get; set; }

    /// <summary>Null when the system made the move.</summary>
    public Guid? ActorMembershipId { get; set; }

    /// <summary>Which sources moved; set exactly when <see cref="ToStatus"/> is NeedsReview.</summary>
    public ContentStaleReasons StaleReasons { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary><see cref="ContentProposalTransitions.Version"/> when written.</summary>
    public string MachineVersion { get; set; } = null!;
}
