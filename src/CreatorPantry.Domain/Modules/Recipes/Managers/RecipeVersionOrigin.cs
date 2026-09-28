namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Where a newly created recipe's version 1 came from.
/// </summary>
/// <param name="Source">How the version came about.</param>
/// <param name="AiProposalId">
/// The proposal it was accepted from, when it was accepted from one. Null for a recipe a creator typed.
/// </param>
/// <remarks>
/// <para>
/// Two facts that must agree, carried together so they cannot be set apart. A version recorded as
/// <see cref="RecipeVersionSource.AiProposalAccepted"/> with no proposal id could not say afterwards what
/// produced it, and a proposal id on a <see cref="RecipeVersionSource.CreatorEdit"/> version would credit a
/// generation for something a creator wrote.
/// </para>
/// <para>
/// Only creation needs this. An edit already records its origin through the call it arrived by —
/// <c>ApplyProposedChangesAsync</c> takes a proposal id and nothing else does — but a recipe can be created
/// two ways, so version 1 has to be told which.
/// </para>
/// </remarks>
/// <remarks>
/// <para>
/// The constructor is private and the factories are the only way in, so the disagreement this type exists to
/// prevent cannot be constructed. A public constructor would permit
/// <c>new RecipeVersionOrigin(AiProposalAccepted, null)</c>, which only
/// <c>CK_RecipeVersions_Proposal_Source</c> would catch — as a failed save, where an unrepresentable state is
/// the better answer.
/// </para>
/// </remarks>
public sealed record RecipeVersionOrigin
{
    private RecipeVersionOrigin(RecipeVersionSource source, Guid? aiProposalId) =>
        (Source, AiProposalId) = (source, aiProposalId);

    public RecipeVersionSource Source { get; }

    public Guid? AiProposalId { get; }

    /// <summary>A recipe a creator typed. What every create before AIREC-002 was.</summary>
    public static RecipeVersionOrigin CreatorEdit { get; } = new(RecipeVersionSource.CreatorEdit, null);

    /// <summary>A recipe created from a generated draft the creator accepted.</summary>
    public static RecipeVersionOrigin AcceptedFrom(Guid aiProposalId) =>
        aiProposalId == Guid.Empty
            ? throw new ArgumentException("An accepted version must name the proposal it came from.", nameof(aiProposalId))
            : new RecipeVersionOrigin(RecipeVersionSource.AiProposalAccepted, aiProposalId);
}
