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
}

internal sealed class IngredientRepository(CreatorPantryDbContext context) : IIngredientRepository
{
    public Task<bool> IsUsableAsync(Guid ingredientId, CancellationToken cancellationToken) =>
        context.Ingredients.AsNoTracking().AnyAsync(ingredient => ingredient.Id == ingredientId && ingredient.IsActive, cancellationToken);

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
