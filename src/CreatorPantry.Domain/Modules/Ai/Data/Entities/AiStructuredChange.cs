using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Data.Entities;

/// <summary>
/// One proposed change, addressed structurally, with the value it would replace and the value it proposes.
/// Together these rows <em>are</em> the server-calculated diff a creator reviews.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Not immutable, and only because of <see cref="Disposition"/>.</strong> Everything describing the
/// change — its target, its kind, both values — is written once by the seam that creates the proposal and
/// never touched again; only the creator's decision is written later. That split is a convention here rather
/// than a guarantee the interceptor enforces, which is the cost of keeping the decision on the row it belongs
/// to instead of in a sixth table.
/// </para>
/// <para>
/// <strong><see cref="BeforeValue"/> is computed by the server</strong> from the pinned source version, never
/// taken from the model. A model-supplied before value is an assertion about content the server already has,
/// and accepting one would let a forged or merely stale claim decide what a diff appears to change.
/// </para>
/// <para>
/// A change carries recipe text, and that is correct: it is the artifact the creator reviews, not a log of
/// one. The privacy rule that forbids storing generated content by default is about diagnostics, and
/// diagnostics live in <see cref="AiExecutionMetadata"/>, which holds none of this.
/// </para>
/// </remarks>
public class AiStructuredChange : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid AiProposalId { get; set; }

    public AiChangeKind ChangeKind { get; set; }

    public AiChangeTargetKind TargetKind { get; set; }

    /// <summary>
    /// The stable id of the row being changed; for an addition, the parent it is added to. Null when the
    /// target is the recipe's own fields, or when a top-level child is being added.
    /// </summary>
    /// <remarks>
    /// Deliberately not constrained by <see cref="TargetKind"/> or <see cref="ChangeKind"/>. The rules that
    /// would make such a constraint correct — which kinds nest under which, whether an addition names its
    /// parent — belong to the structured-output validator and the diff calculator, and encoding a guess at
    /// them here would refuse valid changes for reasons nobody could justify from this file.
    /// </remarks>
    public Guid? TargetId { get; set; }

    /// <summary>
    /// The field being set, e.g. <c>headnote</c>. Required for <see cref="AiChangeKind.Set"/> and meaningless
    /// for every other kind — an addition proposes a whole child, not a field of one.
    /// </summary>
    public string? FieldName { get; set; }

    /// <summary>
    /// The value the pinned source holds today, read from that version by the server. Null when the change
    /// adds something that does not exist yet.
    /// </summary>
    public string? BeforeValue { get; set; }

    /// <summary>
    /// The value proposed. Null when the change removes something, or moves it without rewording it.
    /// </summary>
    public string? AfterValue { get; set; }

    /// <summary>
    /// Where a <see cref="AiChangeKind.Move"/> or <see cref="AiChangeKind.Add"/> puts the child, zero-based
    /// within its parent. Null for the kinds that do not change ordering.
    /// </summary>
    public int? ProposedPosition { get; set; }

    /// <summary>
    /// The order to review these in, which is the order the model offered them. Not the order they are
    /// applied in: applying is the recipe domain's business and follows the recipe's own structure.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>The creator's decision. The one field on this row written after insert.</summary>
    public AiChangeDisposition Disposition { get; set; }

    /// <summary>
    /// When the decision was made, and by whom. Both null while <see cref="Disposition"/> is
    /// <see cref="AiChangeDisposition.Pending"/>.
    /// </summary>
    public DateTimeOffset? DecidedAt { get; set; }

    /// <inheritdoc cref="AiProposalFeedback.MembershipId"/>
    public Guid? DecidedByMembershipId { get; set; }
}
