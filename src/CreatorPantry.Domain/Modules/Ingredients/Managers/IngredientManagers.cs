using System.ComponentModel;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Reference;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Ingredients.Managers;

/// <summary>The ingredient list query, bound from the query string. Global and tenant-less, like every reference query.</summary>
public sealed record IngredientQueryViewModel(
    [property: FromQuery(Name = "search")]
    [property: Description("Free text matched against canonical names and aliases. Terms shorter than two characters are ignored.")]
    string? Search = null,
    [property: FromQuery(Name = "category")]
    [property: Description("Restrict results to one food category, by its code, such as `dairy-and-eggs`.")]
    string? Category = null,
    [property: FromQuery(Name = "cursor")] string? Cursor = null,
    [property: FromQuery(Name = "limit")] int? Limit = null);

/// <summary>An ingredient list query after translation.</summary>
/// <param name="CategoryCode">
/// A food-category code to restrict to, or null. The category itself belongs to the Vocabulary module; only
/// its code crosses, as a string on a foreign key — no type does.
/// </param>
public sealed record IngredientQuery(
    ReferenceSearch? Search,
    string? CategoryCode,
    ReferenceCursor? Cursor,
    int Limit,
    string Scope)
{
    /// <summary>Identifies this exact page for caching.</summary>
    public string CacheKeySegment => ReferenceQueryKey.Build(Search, Cursor, Limit, ("category", CategoryCode));
}

/// <summary>An ingredient, with the aliases that may have caused it to match a search.</summary>
public sealed record IngredientRecord(
    Guid Id,
    string CanonicalName,
    string NormalizedName,
    string? FoodCategoryCode,
    string? DefaultCountUnitCode,
    IReadOnlyList<string> Aliases) : IReferenceRow
{
    public string SortValue => CanonicalName;

    /// <summary>Ingredients have no code; their unique natural key is the normalized name.</summary>
    public string TieBreaker => NormalizedName;
}

/// <summary>
/// One ingredient in the shared catalogue.
/// </summary>
/// <remarks>
/// A reference, never a replacement: matching one of these enriches a recipe line while the creator's entered
/// text stays canonical (recipes.md). <see cref="Aliases"/> is included so a client can show <em>why</em> a
/// search matched — "scallion" finding green onion is otherwise indistinguishable from a bug.
/// </remarks>
/// <param name="DefaultCountUnitCode">
/// The unit to suggest when a creator writes a bare number, such as "2 garlic" proposing cloves. A suggestion
/// only; it never overrides a unit the creator actually wrote.
/// </param>
public sealed record IngredientServiceModel(
    Guid Id,
    string CanonicalName,
    string? FoodCategoryCode,
    string? DefaultCountUnitCode,
    IReadOnlyList<string> Aliases);

/// <summary>Shape rules for this module's list query. See the validator remarks in Measurement for the shared reasoning.</summary>
public sealed class IngredientQueryViewModelValidator : AbstractValidator<IngredientQueryViewModel>
{
    public IngredientQueryViewModelValidator()
    {
        RuleFor(model => model.Search)
            .MaximumLength(ReferencePolicy.SearchMaxLength)
            .WithMessage($"Search terms are limited to {ReferencePolicy.SearchMaxLength} characters.")
            .OverridePropertyName(nameof(IngredientQueryViewModel.Search));

        RuleFor(model => model.Cursor)
            .Must(value => ReferenceCursor.TryDecode(value, out _))
            .WithMessage("Pass the previous page's nextCursor exactly as it was returned.")
            .When(model => !string.IsNullOrEmpty(model.Cursor))
            .OverridePropertyName(nameof(IngredientQueryViewModel.Cursor));

        RuleFor(model => model.Category)
            .Cascade(CascadeMode.Stop)
            .MaximumLength(CodeFormat.CodeMaxLength)
            .Matches(CodeFormat.CodePattern)
            .WithMessage("Enter a food category code, such as 'dairy-and-eggs'.")
            .When(model => !string.IsNullOrWhiteSpace(model.Category))
            .OverridePropertyName(nameof(IngredientQueryViewModel.Category));
    }
}

/// <summary>Turns a validated ingredient view model into the effective domain query.</summary>
public static class IngredientQueryFactory
{
    public static bool TryCreate(IngredientQueryViewModel model, string resource, out IngredientQuery query)
    {
        query = default!;

        var search = ReferencePolicy.NormalizeSearch(model.Search);
        var category = string.IsNullOrWhiteSpace(model.Category) ? null : model.Category.Trim().ToLowerInvariant();
        var scope = ReferenceQueryKey.Scope(resource, search, ("category", category));

        if (!ReferenceCursor.TryResolve(model.Cursor, scope, out var cursor))
        {
            return false;
        }

        query = new IngredientQuery(search, category, cursor, ReferencePolicy.ClampPageSize(model.Limit), scope);

        return true;
    }
}
