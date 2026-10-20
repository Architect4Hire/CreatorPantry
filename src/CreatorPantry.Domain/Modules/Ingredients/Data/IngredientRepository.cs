using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Ingredients.Data;

/// <summary>Reads the shared ingredient catalogue. Global reference data: no workspace filter applies.</summary>
public interface IIngredientRepository
{
    /// <summary>
    /// One page of active ingredients, ordered by canonical name.
    /// </summary>
    /// <returns>The page, and whether another follows it.</returns>
    Task<(IReadOnlyList<IngredientRecord> Rows, bool HasMore)> ListAsync(
        IngredientQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Every active ingredient's canonical name, and every alias of an active ingredient, flattened into one
    /// list for <see cref="IngredientMatcher"/> to resolve candidates against in memory.
    /// </summary>
    Task<IReadOnlyList<IngredientMatchIndexEntry>> ListMatchIndexAsync(CancellationToken cancellationToken);

    /// <summary>Whether the id names an active ingredient — the same "usable" a recipe line may reference.</summary>
    Task<bool> IsUsableAsync(Guid ingredientId, CancellationToken cancellationToken);

    /// <summary>
    /// How many current allergen traits each of the named ingredients has, and how many of those are unreviewed
    /// or still uncertain.
    /// </summary>
    /// <returns>
    /// One row per id that names an active ingredient, in no particular order. An id naming nothing — or an
    /// inactive ingredient — is simply absent, which the caller is free to treat as nothing known.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Counts rather than trait rows, for the reason <see cref="IngredientAllergenTraitCounts"/> gives: the caller
    /// wants one answer per ingredient, and fetching every trait of forty ingredients to reduce them to three
    /// numbers each would read rows nobody looks at.
    /// </para>
    /// <para>
    /// <strong><c>EffectiveFrom</c> is deliberately not consulted.</strong> A future-dated trait is still a
    /// recorded claim, and this read answers whether anybody has finished checking rather than what is true on a
    /// given day — so bringing a clock into a completeness count would add a dependency without changing an
    /// answer. A surface that states presence must consider it; this one does not state presence.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<IngredientAllergenTraitCounts>> CountAllergenTraitsAsync(
        IReadOnlyCollection<Guid> ingredientIds, CancellationToken cancellationToken);

    /// <summary>The active ingredient with this id, untracked, or null.</summary>
    Task<CreatorPantry.Domain.Modules.Ingredients.Data.Entities.Ingredient?> FindActiveAsync(
        Guid ingredientId, CancellationToken cancellationToken);

    /// <summary>The id of the active reference source with this code, or null.</summary>
    Task<Guid?> FindActiveReferenceSourceIdAsync(string code, CancellationToken cancellationToken);

    /// <summary>
    /// The name and food category of each named ingredient, <strong>retired ones included</strong>: a recipe
    /// version that already names an ingredient still names it after the catalogue retires it. An id naming nothing
    /// is absent.
    /// </summary>
    Task<IReadOnlyList<IngredientCategoryServiceModel>> ListCategoriesAsync(
        IReadOnlyCollection<Guid> ingredientIds, CancellationToken cancellationToken);

    /// <summary>
    /// Every <see cref="TraitReviewStatus.Approved"/> trait in effect on <paramref name="asOf"/> among the
    /// ingredients and subjects named, with its source. Every source's, and every date's up to then: choosing among
    /// them is Business's.
    /// </summary>
    Task<(IReadOnlyList<ApprovedAllergenTraitServiceModel> Allergens, IReadOnlyList<ApprovedDietaryTraitServiceModel> Dietary)> ListApprovedTraitsAsync(
        IReadOnlyCollection<Guid> ingredientIds,
        IReadOnlyCollection<Guid> allergenIds,
        IReadOnlyCollection<Guid> dietaryProfileIds,
        DateOnly asOf,
        CancellationToken cancellationToken);
}

internal sealed class IngredientRepository(CreatorPantryDbContext context) : IIngredientRepository
{
    public Task<CreatorPantry.Domain.Modules.Ingredients.Data.Entities.Ingredient?> FindActiveAsync(
        Guid ingredientId, CancellationToken cancellationToken) =>
        context.Ingredients
            .AsNoTracking()
            .FirstOrDefaultAsync(ingredient => ingredient.Id == ingredientId && ingredient.IsActive, cancellationToken);

    public async Task<(IReadOnlyList<ApprovedAllergenTraitServiceModel> Allergens, IReadOnlyList<ApprovedDietaryTraitServiceModel> Dietary)> ListApprovedTraitsAsync(
        IReadOnlyCollection<Guid> ingredientIds,
        IReadOnlyCollection<Guid> allergenIds,
        IReadOnlyCollection<Guid> dietaryProfileIds,
        DateOnly asOf,
        CancellationToken cancellationToken)
    {
        if (ingredientIds.Count == 0)
        {
            return ([], []);
        }

        List<ApprovedAllergenTraitServiceModel> allergens = allergenIds.Count == 0
            ? []
            : await context.IngredientAllergenTraits
                .AsNoTracking()
                .Where(trait => trait.ReviewStatus == TraitReviewStatus.Approved
                    && trait.EffectiveFrom <= asOf
                    && ingredientIds.Contains(trait.IngredientId)
                    && allergenIds.Contains(trait.AllergenId))
                .Join(
                    context.ReferenceSources,
                    trait => trait.ReferenceSourceId,
                    source => source.Id,
                    (trait, source) => new ApprovedAllergenTraitServiceModel(
                        trait.IngredientId,
                        trait.AllergenId,
                        trait.Presence,
                        trait.EvidenceNote,
                        trait.EffectiveFrom,
                        new TraitSourceServiceModel(source.Id, source.Code, source.Name, source.Kind, source.Citation, source.IsActive, source.LicenseCode, source.LicenseUrl)))
                .ToListAsync(cancellationToken);

        List<ApprovedDietaryTraitServiceModel> dietary = dietaryProfileIds.Count == 0
            ? []
            : await context.IngredientDietaryTraits
                .AsNoTracking()
                .Where(trait => trait.ReviewStatus == TraitReviewStatus.Approved
                    && trait.EffectiveFrom <= asOf
                    && ingredientIds.Contains(trait.IngredientId)
                    && dietaryProfileIds.Contains(trait.DietaryProfileId))
                .Join(
                    context.ReferenceSources,
                    trait => trait.ReferenceSourceId,
                    source => source.Id,
                    (trait, source) => new ApprovedDietaryTraitServiceModel(
                        trait.IngredientId,
                        trait.DietaryProfileId,
                        trait.Compatibility,
                        trait.EvidenceNote,
                        trait.EffectiveFrom,
                        new TraitSourceServiceModel(source.Id, source.Code, source.Name, source.Kind, source.Citation, source.IsActive, source.LicenseCode, source.LicenseUrl)))
                .ToListAsync(cancellationToken);

        return (allergens, dietary);
    }

    public async Task<Guid?> FindActiveReferenceSourceIdAsync(string code, CancellationToken cancellationToken) =>
        await context.ReferenceSources
            .Where(source => source.Code == code && source.IsActive)
            .Select(source => (Guid?)source.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<IngredientAllergenTraitCounts>> CountAllergenTraitsAsync(
        IReadOnlyCollection<Guid> ingredientIds,
        CancellationToken cancellationToken)
    {
        if (ingredientIds.Count == 0)
        {
            // No query at all rather than one with an empty IN list. The caller asking about no ingredients is
            // ordinary — a recipe whose every line is unmatched has none to ask about.
            return [];
        }

        // Three correlated counts over IngredientAllergenTraits, which is global reference data: no workspace
        // filter applies to any of this, and none should be written by hand.
        return await context.Ingredients.AsNoTracking()
            .Where(ingredient => ingredientIds.Contains(ingredient.Id) && ingredient.IsActive)
            .Select(ingredient => new IngredientAllergenTraitCounts(
                ingredient.Id,
                ingredient.CanonicalName,
                context.IngredientAllergenTraits.Count(trait =>
                    trait.IngredientId == ingredient.Id
                    && (trait.ReviewStatus == TraitReviewStatus.Approved
                        || trait.ReviewStatus == TraitReviewStatus.Unreviewed)),
                context.IngredientAllergenTraits.Count(trait =>
                    trait.IngredientId == ingredient.Id
                    && trait.ReviewStatus == TraitReviewStatus.Unreviewed),
                context.IngredientAllergenTraits.Count(trait =>
                    trait.IngredientId == ingredient.Id
                    && trait.ReviewStatus == TraitReviewStatus.Approved
                    && (trait.Presence == AllergenPresence.Unknown
                        || trait.Presence == AllergenPresence.PossiblePresence))))
            .ToListAsync(cancellationToken);
    }

    public Task<bool> IsUsableAsync(Guid ingredientId, CancellationToken cancellationToken) =>
        context.Ingredients.AsNoTracking().AnyAsync(ingredient => ingredient.Id == ingredientId && ingredient.IsActive, cancellationToken);

    public async Task<IReadOnlyList<IngredientCategoryServiceModel>> ListCategoriesAsync(
        IReadOnlyCollection<Guid> ingredientIds, CancellationToken cancellationToken)
    {
        if (ingredientIds.Count == 0)
        {
            return [];
        }

        // The category code is projected through its own table, as ListAsync does, so a renamed code cannot go stale.
        return await context.Ingredients.AsNoTracking()
            .Where(ingredient => ingredientIds.Contains(ingredient.Id))
            .Select(ingredient => new IngredientCategoryServiceModel(
                ingredient.Id,
                ingredient.CanonicalName,
                context.FoodCategories
                    .Where(category => category.Id == ingredient.FoodCategoryId)
                    .Select(category => category.Code)
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<IngredientRecord> Rows, bool HasMore)> ListAsync(
        IngredientQuery query, CancellationToken cancellationToken)
    {
        var ingredients = context.Ingredients.AsNoTracking().Where(ingredient => ingredient.IsActive);

        if (query.Search is { } search)
        {
            // SearchText is the denormalized name-plus-aliases column built for exactly this: one LIKE instead
            // of a join, so "scallion" finds green onion without the alias table being read at all.
            ingredients = ingredients.Where(ingredient => ingredient.SearchText.Contains(search.Normalized));
        }

        if (query.CategoryCode is { } categoryCode)
        {
            var categoryId = context.FoodCategories
                .Where(category => category.Code == categoryCode)
                .Select(category => (Guid?)category.Id);

            ingredients = ingredients.Where(ingredient => categoryId.Contains(ingredient.FoodCategoryId));
        }

        if (query.Cursor is { } cursor)
        {
            ingredients = ingredients.Where(ingredient =>
                string.Compare(ingredient.CanonicalName, cursor.SortValue) > 0
                || (ingredient.CanonicalName == cursor.SortValue
                    && string.Compare(ingredient.NormalizedName, cursor.TieBreaker) > 0));
        }

        // The category and unit codes are projected through their own tables rather than stored here, so a
        // renamed code cannot go stale in this response.
        var fetched = await ingredients
            .OrderBy(ingredient => ingredient.CanonicalName)
            .ThenBy(ingredient => ingredient.NormalizedName)
            .Take(query.Limit + 1)
            .Select(ingredient => new IngredientRecord(
                ingredient.Id,
                ingredient.CanonicalName,
                ingredient.NormalizedName,
                context.FoodCategories
                    .Where(category => category.Id == ingredient.FoodCategoryId)
                    .Select(category => category.Code)
                    .FirstOrDefault(),
                context.MeasurementUnits
                    .Where(unit => unit.Id == ingredient.DefaultCountUnitId)
                    .Select(unit => unit.Code)
                    .FirstOrDefault(),
                context.IngredientAliases
                    .Where(alias => alias.IngredientId == ingredient.Id)
                    .OrderBy(alias => alias.Alias)
                    .Select(alias => alias.Alias)
                    .ToList()))
            .ToListAsync(cancellationToken);

        return fetched.ToPage(query.Limit);
    }

    public async Task<IReadOnlyList<IngredientMatchIndexEntry>> ListMatchIndexAsync(CancellationToken cancellationToken)
    {
        var canonical = await context.Ingredients.AsNoTracking()
            .Where(ingredient => ingredient.IsActive)
            .Select(ingredient => new IngredientMatchIndexEntry(
                ingredient.Id, ingredient.CanonicalName, ingredient.NormalizedName, IngredientMatchKind.CanonicalName))
            .ToListAsync(cancellationToken);

        // A retired ingredient's alias is excluded the same way ListAsync excludes the ingredient itself: it
        // stays readable for what already resolved to it, but it does not newly match.
        var activeIds = canonical.Select(entry => entry.IngredientId).ToHashSet();

        var aliases = await context.IngredientAliases.AsNoTracking()
            .Where(alias => activeIds.Contains(alias.IngredientId))
            .Select(alias => new { alias.IngredientId, alias.NormalizedAlias })
            .ToListAsync(cancellationToken);

        var namesById = canonical.ToDictionary(entry => entry.IngredientId, entry => entry.CanonicalName);

        var aliasEntries = aliases.Select(alias => new IngredientMatchIndexEntry(
            alias.IngredientId, namesById[alias.IngredientId], alias.NormalizedAlias, IngredientMatchKind.Alias));

        return [.. canonical, .. aliasEntries];
    }
}
