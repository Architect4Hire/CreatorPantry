using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ingredients.Data;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ingredients.Facade;
public interface IIngredientFacade
{
    Task<OperationResult<CursorPageServiceModel<IngredientServiceModel>>> ListIngredientsAsync(
        IngredientQueryViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Matches each candidate string against the ingredient catalogue by exact/alias normalization, ranked by
    /// precedence with unresolved ambiguity called out rather than guessed (ING-001, AIREC-GR-003).
    /// </summary>
    /// <remarks>
    /// Read-only and global: no workspace context is required or accepted, matching every other read in this
    /// module. Results preserve the order of <paramref name="candidateTexts"/>.
    /// </remarks>
    Task<OperationResult<IReadOnlyList<IngredientMatchResult>>> ResolveCandidatesAsync(
        IReadOnlyList<string> candidateTexts, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the id names an active ingredient — the same question <c>IVocabularyFacade.IsUsableAsync</c>
    /// answers for the four controlled vocabularies, asked of this module's own catalogue instead. Used by
    /// another module's write seam (a recipe's ingredient lines, say) to verify a submitted reference before
    /// it reaches a foreign key.
    /// </summary>
    Task<bool> IsUsableAsync(Guid ingredientId, CancellationToken cancellationToken);

    /// <summary>
    /// Which of the named ingredients nobody has finished recording allergen information for.
    /// </summary>
    /// <param name="ingredientIds">
    /// The ingredients to ask about — typically the vocabulary references a recipe's matched lines carry. An empty
    /// collection is answered with an empty list and reads nothing.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// One entry per ingredient with a gap, and nothing for the ones that are complete. See
    /// <see cref="IngredientAllergenReviewServiceModel"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The cross-module entry point another module uses to ask how far the allergen records have got — added for
    /// the recipe readiness evaluation, which must be able to say "this has not been checked" without being able
    /// to read this module's tables.
    /// </para>
    /// <para>
    /// <strong>This answers a question about records, never about food.</strong> An empty result means every named
    /// ingredient has reviewed allergen traits recorded; it does not mean the recipe is free of any allergen, and a
    /// caller that presented it that way would be making exactly the claim recipes.md forbids. Nothing here names
    /// an allergen, which is the structural reason it cannot be turned into a finding about a dish.
    /// </para>
    /// <para>
    /// Uncached, unlike the catalogue reads above. The traits behind this change as sources are reviewed, and a
    /// stale "already checked" is the one answer that would matter — so the read is cheap and current rather than
    /// cached and occasionally reassuring about work nobody has done.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<IngredientAllergenReviewServiceModel>> FindAllergenReviewGapsAsync(
        IReadOnlyCollection<Guid> ingredientIds, CancellationToken cancellationToken);

    /// <summary>
    /// Approved nutrition reference foods for one platform ingredient, searched by its canonical name (14.2).
    /// </summary>
    /// <remarks>
    /// The cross-module way to reach the evidence sources: a caller names a platform ingredient, never text of
    /// its own, so a creator's words cannot reach a source or the shared cache (B-33). An inactive or unknown
    /// ingredient answers <see cref="NutritionReferenceStatus.NotFound"/> without a search.
    /// </remarks>
    Task<NutritionReferenceSearchServiceModel> SearchNutritionReferencesAsync(
        Guid ingredientId, CancellationToken cancellationToken);

    /// <summary>One approved nutrition reference food, by its source and the source's id for it (14.2).</summary>
    Task<NutritionReferenceServiceModel> GetNutritionReferenceAsync(
        string sourceCode, string externalRecordId, CancellationToken cancellationToken);

    /// <summary>
    /// The approved allergen and dietary traits in effect on <paramref name="asOf"/> for the ingredients, allergens
    /// and profiles named (14.4).
    /// </summary>
    /// <remarks>
    /// Approved only: an unreviewed, rejected or superseded trait is on record without being actionable, and an
    /// analysis must not see it. A trait dated after <paramref name="asOf"/> is not yet in effect. Uncached, for
    /// the reason <see cref="FindAllergenReviewGapsAsync"/> gives.
    /// </remarks>
    Task<IngredientTraitEvidenceServiceModel> FindApprovedTraitsAsync(
        IReadOnlyCollection<Guid> ingredientIds,
        IReadOnlyCollection<Guid> allergenIds,
        IReadOnlyCollection<Guid> dietaryProfileIds,
        DateOnly asOf,
        CancellationToken cancellationToken);

    /// <summary>
    /// The catalogue name and food category of each named ingredient, retired ones included (14.5). An id naming
    /// nothing is absent; an empty collection reads nothing.
    /// </summary>
    /// <remarks>
    /// Retired entries answer because the caller is reading what a recipe version already names, not choosing
    /// something new. A category is a classification for a reader to act on, never a statement about the food.
    /// </remarks>
    Task<IReadOnlyList<IngredientCategoryServiceModel>> ListCategoriesAsync(
        IReadOnlyCollection<Guid> ingredientIds, CancellationToken cancellationToken);

    /// <summary>
    /// Whether evidence of <paramref name="kind"/> can be had on this host, and from which approved source (14.5).
    /// </summary>
    /// <remarks>
    /// A statement about sources, not about any food: <see cref="ReferenceEvidenceAvailability.NoApprovedSource"/>
    /// means nothing is approved for the kind (B-33), and its evidence is unknown on every host.
    /// </remarks>
    ReferenceEvidenceCoverageServiceModel DescribeEvidenceCoverage(ReferenceEvidenceKind kind);
}

