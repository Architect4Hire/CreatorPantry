namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The editorial facts about a version a write is about to capture — why it exists and what it claims about
/// itself.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is Business's decision. The DataLayer supplies only what is mechanical: the version
/// number (1 on a create, one past the current version on an edit), the parent version, the concurrency
/// token the edit was composed against, and the timestamp and actor, which are copied from the recipe so
/// the two rows cannot disagree about when it happened or who did it.
/// </para>
/// <para>
/// <see cref="RestoredFromVersionId"/> is here rather than among those mechanical values, although it is an
/// id the DataLayer could see, because which version a restore drew from is the decision the operation
/// <em>is</em> — the DataLayer is handed a reconciled aggregate and cannot tell where its content came from.
/// It defaults to <c>null</c> so that no existing caller has to say "this was not a restore".
/// </para>
/// <para>
/// One type for both writes rather than one per write. The distinction between a first version and a later
/// one is entirely in those mechanical values, none of which a caller supplies, so a second record would
/// have had the same three members and invited them to drift.
/// </para>
/// </remarks>
/// <param name="RestoredFromVersionId">
/// The version whose content this one was copied from, on a restore, and <c>null</c> on every other write.
/// </param>
/// <param name="AiProposalId">
/// The proposal a creator accepted, when <c>Source</c> is <c>AiProposalAccepted</c>, and <c>null</c> on every
/// other write. The two move together: a check constraint on the table refuses a proposal id without that
/// source and that source without a proposal id, so provenance cannot be half-recorded.
/// </param>
public sealed record RecipeVersionFacts(
    RecipeVersionSource Source,
    RecipeVersionReadiness Readiness,
    string? Reason,
    Guid? RestoredFromVersionId = null,
    Guid? AiProposalId = null);
