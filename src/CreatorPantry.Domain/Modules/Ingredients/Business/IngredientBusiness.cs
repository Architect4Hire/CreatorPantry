using CreatorPantry.Domain.Modules.Ingredients.Data;
using CreatorPantry.Domain.Modules.Ingredients.Gateways;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ingredients.Business;
internal sealed class IngredientBusiness(
    IIngredientDataLayer dataLayer,
    IReferenceEvidenceDataLayer evidence) : IIngredientBusiness
{
    public async Task<NutritionReferenceSearchServiceModel> SearchNutritionReferencesAsync(
        Guid ingredientId, CancellationToken cancellationToken)
    {
        if (evidence.Describe(ReferenceEvidenceKind.Nutrition) is { Status: not EvidenceCoverageStatus.Available } coverage)
        {
            return new NutritionReferenceSearchServiceModel(StatusOf(coverage.Status), null, []);
        }

        // The search term is the catalogue's own name for the ingredient, read here, and nothing a caller wrote.
        if (await dataLayer.FindActiveIngredientAsync(ingredientId, cancellationToken) is not { } ingredient)
        {
            return new NutritionReferenceSearchServiceModel(NutritionReferenceStatus.NotFound, null, []);
        }

        var result = await evidence.SearchFoodsAsync(ReferenceFoodSearch.ForIngredient(ingredient), cancellationToken);

        return new NutritionReferenceSearchServiceModel(
            StatusOf(result.Outcome),
            ingredient.CanonicalName,
            [.. result.Candidates.Select(candidate => new NutritionReferenceCandidateServiceModel(
                candidate.SourceCode, candidate.ExternalRecordId, candidate.Description, candidate.SourceVersion))]);
    }

    public async Task<NutritionReferenceServiceModel> GetNutritionReferenceAsync(
        string sourceCode, string externalRecordId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(externalRecordId);

        var coverage = evidence.Describe(ReferenceEvidenceKind.Nutrition);
        if (coverage.Status is not EvidenceCoverageStatus.Available)
        {
            return new NutritionReferenceServiceModel(StatusOf(coverage.Status));
        }

        // A source that is not the one approved for nutrition is not asked at all.
        if (!string.Equals(sourceCode, coverage.SourceCode, StringComparison.Ordinal))
        {
            return new NutritionReferenceServiceModel(NutritionReferenceStatus.NoApprovedSource);
        }

        var fetched = await evidence.GetFoodAsync(externalRecordId, cancellationToken);
        if (fetched is not { Outcome: ReferenceEvidenceOutcome.Found, Food: { } food })
        {
            return new NutritionReferenceServiceModel(StatusOf(fetched.Outcome));
        }

        // The row every fact from this source cites. Seeded in every environment; absent, nothing can cite it.
        if (await dataLayer.FindActiveReferenceSourceIdAsync(food.Provenance.SourceCode, cancellationToken) is not { } sourceId)
        {
            return new NutritionReferenceServiceModel(NutritionReferenceStatus.NoApprovedSource);
        }

        return new NutritionReferenceServiceModel(
            NutritionReferenceStatus.Found,
            sourceId,
            food.Provenance.SourceCode,
            food.Provenance.ExternalRecordId,
            food.Description,
            food.Provenance.SourceVersion,
            [.. food.Portions.Select(portion => new NutritionReferencePortionServiceModel(
                portion.Amount, portion.GramWeight, portion.VolumeUnitCode, portion.Condition, portion.SourceLabel))],
            [.. food.Nutrients.Select(nutrient => new NutritionReferenceNutrientServiceModel(
                nutrient.Code, nutrient.AmountPer100Grams, nutrient.Unit, nutrient.SourceLabel, nutrient.Derivation))],
            food.Provenance.Citation,
            food.Provenance.LicenseCode,
            food.Provenance.LicenseUrl);
    }

    public async Task<IngredientTraitEvidenceServiceModel> FindApprovedTraitsAsync(
        IReadOnlyCollection<Guid> ingredientIds,
        IReadOnlyCollection<Guid> allergenIds,
        IReadOnlyCollection<Guid> dietaryProfileIds,
        DateOnly asOf,
        CancellationToken cancellationToken)
    {
        var (allergens, dietary) = await dataLayer.ListApprovedTraitsAsync(
            ingredientIds, allergenIds, dietaryProfileIds, asOf, cancellationToken);

        // A source's latest word in effect supersedes its own earlier one — never another source's, whose
        // disagreement is exactly what an analysis has to be able to see. A later "consulted, does not say" is no
        // word at all, so it never supersedes an earlier warning from the same source: it is dropped first.
        return new IngredientTraitEvidenceServiceModel(
            [.. allergens
                .Where(trait => trait.Presence is not AllergenPresence.Unknown)
                .GroupBy(trait => (trait.IngredientId, trait.AllergenId, trait.Source.ReferenceSourceId))
                .Select(group => group.MaxBy(trait => trait.EffectiveFrom)!)],
            [.. dietary
                .Where(trait => trait.Compatibility is not DietaryCompatibility.Unknown)
                .GroupBy(trait => (trait.IngredientId, trait.DietaryProfileId, trait.Source.ReferenceSourceId))
                .Select(group => group.MaxBy(trait => trait.EffectiveFrom)!)]);
    }

    private static NutritionReferenceStatus StatusOf(EvidenceCoverageStatus status) => status switch
    {
        EvidenceCoverageStatus.NotConfigured => NutritionReferenceStatus.NotConfigured,
        EvidenceCoverageStatus.NoApprovedSource => NutritionReferenceStatus.NoApprovedSource,
        _ => NutritionReferenceStatus.Found,
    };

    private static NutritionReferenceStatus StatusOf(ReferenceEvidenceOutcome outcome) => outcome switch
    {
        ReferenceEvidenceOutcome.Found => NutritionReferenceStatus.Found,
        ReferenceEvidenceOutcome.NotFound => NutritionReferenceStatus.NotFound,
        ReferenceEvidenceOutcome.NoApprovedSource => NutritionReferenceStatus.NoApprovedSource,
        ReferenceEvidenceOutcome.NotConfigured => NutritionReferenceStatus.NotConfigured,
        _ => NutritionReferenceStatus.Unavailable,
    };

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

