namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// One warning attached to a proposal about a recipe that still has undecided changes.
/// </summary>
/// <remarks>
/// <para>
/// "Outstanding" is a property of the <em>proposal</em>, not of the warning: a warning stays on its proposal
/// forever, because <see cref="Data.Entities.AiWarning"/> is immutable, so a proposal whose every change has
/// been accepted or rejected has warnings that are history rather than work. This shape only carries the ones
/// a creator has still not responded to.
/// </para>
/// <para>
/// <see cref="Message"/> is model-written text about the workspace's own recipe. It travels to the caller
/// because a warning nobody can read is not a warning, and it is never logged — ai.md keeps generated content
/// out of logs by default.
/// </para>
/// </remarks>
/// <param name="AiProposalId">
/// The proposal carrying it, so a caller can link a reader to the proposal rather than restate its contents.
/// </param>
public sealed record AiOutstandingWarningServiceModel(
    AiWarningKind Kind,
    string Message,
    Guid AiProposalId);

/// <summary>
/// What AI work about one recipe the creator has still not responded to.
/// </summary>
/// <remarks>
/// <para>
/// <strong>An Ai-module concept, deliberately not a readiness one.</strong> It answers "what is still
/// undecided here", which this module owns; what that means for whether a recipe may be published is the
/// Recipes module's judgement, made by rules that live there. Naming this after readiness would have put
/// another module's policy in this one's vocabulary.
/// </para>
/// <para>
/// Both numbers count within the resolved workspace, through the global query filter. A recipe id from another
/// workspace — or one that names nothing — is answered with zeros rather than an error: this read is not the
/// place a recipe's existence is settled, and answering differently would let it be used to probe for one.
/// </para>
/// </remarks>
/// <param name="PendingChangeCount">
/// How many proposed changes across every proposal about this recipe are still
/// <see cref="AiChangeDisposition.Pending"/>. Zero means the creator has been through all of it.
/// </param>
/// <param name="ProposalIds">
/// The proposals holding those changes, ordered and distinct.
/// <para>
/// Published beside the count rather than left to be inferred from <paramref name="Warnings"/>, because a
/// proposal can hold undecided changes and raise no warning at all — so a caller with only the count and the
/// warnings can know that three changes are outstanding and have nothing to link a reader to. A count that
/// cannot name its own records is not something a caller can explain.
/// </para>
/// </param>
/// <param name="Warnings">
/// The warnings on those still-undecided proposals, in a stable order: by proposal, then by the warning's own
/// <c>SortOrder</c>. Empty when nothing is outstanding, and empty is also the honest answer for a proposal that
/// raised no warnings.
/// </param>
public sealed record AiOutstandingSummaryServiceModel(
    int PendingChangeCount,
    IReadOnlyList<Guid> ProposalIds,
    IReadOnlyList<AiOutstandingWarningServiceModel> Warnings);
