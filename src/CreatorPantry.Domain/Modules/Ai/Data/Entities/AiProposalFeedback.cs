using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Ai.Data.Entities;

/// <summary>
/// A creator's own words about a proposal: whether it was useful, and why.
/// </summary>
/// <remarks>
/// <para>
/// Append-only and immutable. A creator who changes their mind leaves another row, so the record shows what
/// they thought when they thought it — which is the only form of this data worth having for improving a
/// prompt later.
/// </para>
/// <para>
/// Several rows per proposal are allowed, from the same member or different ones. Nothing here is aggregated
/// or scored; that would be a product decision this table does not need to anticipate.
/// </para>
/// </remarks>
public class AiProposalFeedback : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid AiProposalId { get; set; }

    /// <summary>
    /// The <c>WorkspaceMembership</c> of the creator who left this, not their Identity user id. Membership is
    /// the workspace-scoped identity (auth.md), and it is taken from the resolved context rather than a
    /// request field.
    /// </summary>
    public Guid MembershipId { get; set; }

    /// <summary>Whether the proposal was useful. Null when the creator only left words.</summary>
    public bool? WasHelpful { get; set; }

    /// <summary>The creator's comment. Null when they only answered the question.</summary>
    public string? Comment { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
