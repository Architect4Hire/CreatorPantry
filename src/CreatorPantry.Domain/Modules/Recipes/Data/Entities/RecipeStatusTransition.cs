using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Entities;

/// <summary>
/// One move a recipe made between editorial states: from where, to where, by whom, when, and why
/// (TESTRUN-005).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Immutable</strong> by way of <see cref="IImmutableRecord"/>: every update and delete is refused at
/// <c>SaveChanges</c>, whatever code path arrives there. A transition is a thing that happened at a moment,
/// and a history that can be rewritten is not a history — the same argument
/// <see cref="TestIssueResolution"/> makes about a decision, applied to the record of a decision's effect.
/// </para>
/// <para>
/// <strong>The recipe's <c>Status</c> is the current state and this is how it got there.</strong> Neither is
/// derivable from the other cheaply enough to drop: replaying the transitions to answer "what is this recipe
/// now" would be a read on every list row, and a status with no history behind it cannot answer who approved
/// a recipe or what was said when it came back. The write seam sets both in one transaction, so they cannot
/// disagree.
/// </para>
/// <para>
/// <strong>This is not the audit log, and the two say different things.</strong> An audit entry's summary is
/// required to stay safe to display and is therefore written by the system;
/// <see cref="Reason"/> here is the creator's own words, stored as domain content the way a version's reason
/// is. That is why a reason can live on this row although <c>RecipeLifecycleViewModel</c> declined to put one
/// in the audit summary. Both are written for a transition: the audit entry records that it happened, and this
/// records what it was.
/// </para>
/// </remarks>
public class RecipeStatusTransition : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <inheritdoc cref="RecipeTestRun.RecipeId"/>
    public Guid RecipeId { get; set; }

    /// <summary>The state the recipe was in. Recorded rather than inferred from the previous row.</summary>
    /// <remarks>
    /// Storing it makes each row readable on its own and makes a gap in the history detectable — a row whose
    /// <see cref="FromStatus"/> is not the previous row's <see cref="ToStatus"/> says something was missed,
    /// which a chain of "to" values alone could never reveal.
    /// </remarks>
    public RecipeStatus FromStatus { get; set; }

    /// <summary>The state the recipe moved to.</summary>
    public RecipeStatus ToStatus { get; set; }

    /// <summary>
    /// Why, in the words of whoever moved it. Required on a reopen and optional on every other move; see
    /// <see cref="RecipeStatusTransitionRule.RequiresReason"/>.
    /// </summary>
    /// <remarks>
    /// Creator text, which means it is never assembled into a log line or an audit summary. The one exception
    /// is an edit-driven reopen, where the sentence is
    /// <see cref="RecipeStatusTransitions.EditReopenReason"/> because no human typed one.
    /// </remarks>
    public string? Reason { get; set; }

    /// <inheritdoc cref="Recipe.CreatedByMembershipId"/>
    public Guid ActorMembershipId { get; set; }

    /// <summary>When the move happened, from the server's clock.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>
    /// <see cref="RecipeStatusTransitions.Version"/> as it stood when this row was written.
    /// </summary>
    /// <remarks>
    /// So a history stays interpretable after the machine changes shape. A row saying
    /// <c>ReadyForReview → Approved</c> under version 1.0.0 means what 1.0.0 said that move required, which is
    /// the only way to read an old approval honestly once the gate has moved.
    /// </remarks>
    public string MachineVersion { get; set; } = null!;

    /// <summary>
    /// <see cref="RecipeReadinessCatalogue.Version"/> of the evaluation that cleared this move, on an approval,
    /// and <c>null</c> on every other move.
    /// </summary>
    /// <remarks>
    /// Recorded because "there were no blockers" is only meaningful alongside which rules were asked. An
    /// approval under a catalogue that has since gained a rule was not held to it, and this is what says so.
    /// </remarks>
    public string? ReadinessRuleSetVersion { get; set; }

    /// <summary>
    /// The version the readiness evaluation was made of, on an approval, and <c>null</c> otherwise.
    /// </summary>
    /// <remarks>
    /// The recipe's latest version at evaluation time, which is what the rules actually read. Distinct from
    /// <see cref="CreatedVersionId"/>, the version this transition then wrote: the first is the content that
    /// was judged, the second is the immutable copy the approval now names. They are usually adjacent and
    /// there is no value in assuming it.
    /// </remarks>
    public Guid? ReadinessEvaluatedVersionId { get; set; }

    /// <summary>
    /// The version this transition captured, on an approval, and <c>null</c> on every other move.
    /// </summary>
    /// <remarks>
    /// What makes an approval a claim about fixed words. See
    /// <see cref="RecipeStatusTransitionRule.WritesVersion"/>.
    /// </remarks>
    public Guid? CreatedVersionId { get; set; }
}
