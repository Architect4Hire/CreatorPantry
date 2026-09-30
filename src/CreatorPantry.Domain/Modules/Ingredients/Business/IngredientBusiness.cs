using CreatorPantry.Domain.Modules.Ingredients.Data;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ingredients.Business;
internal sealed class IngredientBusiness(IIngredientDataLayer dataLayer) : IIngredientBusiness
{
    public async Task<CursorPageServiceModel<IngredientServiceModel>> ListIngredientsAsync(
        IngredientQuery query, CancellationToken cancellationToken)
    {
        var (rows, hasMore) = await dataLayer.ListIngredientsAsync(query, cancellationToken);

        return PageBuilder.Build(rows, hasMore, query.Scope, row => new IngredientServiceModel(
            row.Id, row.CanonicalName, row.FoodCategoryCode, row.DefaultCountUnitCode, row.Aliases));
    }

    public Task<IReadOnlyList<IngredientMatchIndexEntry>> LoadMatchIndexAsync(CancellationToken cancellationToken) =>
        dataLayer.LoadMatchIndexAsync(cancellationToken);

    public IReadOnlyList<IngredientMatchResult> ResolveCandidates(
        IReadOnlyList<string> candidateTexts, IReadOnlyList<IngredientMatchIndexEntry> index)
    {
        var lookup = index.ToLookup(entry => entry.NormalizedText, StringComparer.Ordinal);

        return candidateTexts.Select(text => IngredientMatcher.Resolve(text, lookup)).ToList();
    }

    public Task<bool> IsUsableAsync(Guid ingredientId, CancellationToken cancellationToken) =>
        dataLayer.IsUsableAsync(ingredientId, cancellationToken);

    public async Task<IReadOnlyList<IngredientAllergenReviewServiceModel>> FindAllergenReviewGapsAsync(
        IReadOnlyCollection<Guid> ingredientIds,
        CancellationToken cancellationToken)
    {
        var counted = await dataLayer.CountAllergenTraitsAsync(ingredientIds, cancellationToken);
        var byId = counted.ToDictionary(row => row.IngredientId);

        var gaps = new List<IngredientAllergenReviewServiceModel>();

        // Iterating the ids the caller asked about rather than the rows that came back, so an id the catalogue
        // did not vouch for becomes a reported gap instead of disappearing. Silence would read as "checked".
        foreach (var ingredientId in ingredientIds.Distinct())
        {
            if (!byId.TryGetValue(ingredientId, out var counts))
            {
                gaps.Add(new IngredientAllergenReviewServiceModel(
                    ingredientId, string.Empty, IngredientAllergenReviewState.NoTraitsRecorded));

                continue;
            }

            if (StateOf(counts) is { } state)
            {
                gaps.Add(new IngredientAllergenReviewServiceModel(ingredientId, counts.CanonicalName, state));
            }
        }

        return gaps;
    }

    /// <summary>
    /// The gap one ingredient's counts describe, or <c>null</c> when there is none.
    /// </summary>
    /// <remarks>
    /// The precedence <see cref="IngredientAllergenReviewState"/> documents, in the order it documents it:
    /// nothing recorded outranks a claim nobody has reviewed, which outranks a reviewed claim that is itself
    /// unsettled. Only the last branch is a clean answer, and it is still only a statement about the records.
    /// </remarks>
    private static IngredientAllergenReviewState? StateOf(IngredientAllergenTraitCounts counts) => counts switch
    {
        { CurrentTraitCount: 0 } => IngredientAllergenReviewState.NoTraitsRecorded,
        { AwaitingReviewCount: > 0 } => IngredientAllergenReviewState.AwaitingReview,
        { UncertainReviewedCount: > 0 } => IngredientAllergenReviewState.PresenceUncertain,
        _ => null,
    };
}

