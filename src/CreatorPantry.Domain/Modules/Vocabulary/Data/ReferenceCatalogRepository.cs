using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Vocabulary.Data;

/// <summary>
/// Reads the three reference tables that predate — or deliberately sit outside —
/// <see cref="ControlledVocabulary"/>: food categories, dietary profiles, and allergens. Global reference
/// data; no workspace filter applies.
/// </summary>
/// <remarks>
/// None of the three has an alias table, so a search here matches display name and code only.
/// </remarks>
public interface IReferenceCatalogRepository
{
    Task<(IReadOnlyList<ReferenceEntryRecord> Rows, bool HasMore)> ListFoodCategoriesAsync(
        ReferenceQuery query, CancellationToken cancellationToken);

    /// <summary>Dietary profiles carry a required description, so they have their own record type.</summary>
    Task<(IReadOnlyList<DescribedReferenceEntryRecord> Rows, bool HasMore)> ListDietaryProfilesAsync(
        ReferenceQuery query, CancellationToken cancellationToken);

    /// <inheritdoc cref="ListDietaryProfilesAsync"/>
    Task<(IReadOnlyList<DescribedReferenceEntryRecord> Rows, bool HasMore)> ListAllergensAsync(
        ReferenceQuery query, CancellationToken cancellationToken);
}

internal sealed class ReferenceCatalogRepository(CreatorPantryDbContext context) : IReferenceCatalogRepository
{
    public async Task<(IReadOnlyList<ReferenceEntryRecord> Rows, bool HasMore)> ListFoodCategoriesAsync(
        ReferenceQuery query, CancellationToken cancellationToken)
    {
        var categories = context.FoodCategories.AsNoTracking().Where(category => category.IsActive);

        if (query.Search is { } search)
        {
            categories = categories.Where(category =>
                category.DisplayName.ToLower().Contains(search.Raw) || category.Code.Contains(search.Raw));
        }

        if (query.Cursor is { } cursor)
        {
            categories = categories.Where(category =>
                string.Compare(category.DisplayName, cursor.SortValue) > 0
                || (category.DisplayName == cursor.SortValue
                    && string.Compare(category.Code, cursor.TieBreaker) > 0));
        }

        var fetched = await categories
            .OrderBy(category => category.DisplayName)
            .ThenBy(category => category.Code)
            .Take(query.Limit + 1)
            .Select(category => new ReferenceEntryRecord(category.Id, category.Code, category.DisplayName))
            .ToListAsync(cancellationToken);

        return fetched.ToPage(query.Limit);
    }

    public async Task<(IReadOnlyList<DescribedReferenceEntryRecord> Rows, bool HasMore)> ListDietaryProfilesAsync(
        ReferenceQuery query, CancellationToken cancellationToken)
    {
        var profiles = context.DietaryProfiles.AsNoTracking().Where(profile => profile.IsActive);

        if (query.Search is { } search)
        {
            profiles = profiles.Where(profile =>
                profile.DisplayName.ToLower().Contains(search.Raw) || profile.Code.Contains(search.Raw));
        }

        if (query.Cursor is { } cursor)
        {
            profiles = profiles.Where(profile =>
                string.Compare(profile.DisplayName, cursor.SortValue) > 0
                || (profile.DisplayName == cursor.SortValue && string.Compare(profile.Code, cursor.TieBreaker) > 0));
        }

        var fetched = await profiles
            .OrderBy(profile => profile.DisplayName)
            .ThenBy(profile => profile.Code)
            .Take(query.Limit + 1)
            .Select(profile => new DescribedReferenceEntryRecord(
                profile.Id, profile.Code, profile.DisplayName, profile.Description))
            .ToListAsync(cancellationToken);

        return fetched.ToPage(query.Limit);
    }

    public async Task<(IReadOnlyList<DescribedReferenceEntryRecord> Rows, bool HasMore)> ListAllergensAsync(
        ReferenceQuery query, CancellationToken cancellationToken)
    {
        var allergens = context.Allergens.AsNoTracking().Where(allergen => allergen.IsActive);

        if (query.Search is { } search)
        {
            allergens = allergens.Where(allergen =>
                allergen.DisplayName.ToLower().Contains(search.Raw) || allergen.Code.Contains(search.Raw));
        }

        if (query.Cursor is { } cursor)
        {
            allergens = allergens.Where(allergen =>
                string.Compare(allergen.DisplayName, cursor.SortValue) > 0
                || (allergen.DisplayName == cursor.SortValue
                    && string.Compare(allergen.Code, cursor.TieBreaker) > 0));
        }

        var fetched = await allergens
            .OrderBy(allergen => allergen.DisplayName)
            .ThenBy(allergen => allergen.Code)
            .Take(query.Limit + 1)
            .Select(allergen => new DescribedReferenceEntryRecord(
                allergen.Id, allergen.Code, allergen.DisplayName, allergen.Description))
            .ToListAsync(cancellationToken);

        return fetched.ToPage(query.Limit);
    }
}
