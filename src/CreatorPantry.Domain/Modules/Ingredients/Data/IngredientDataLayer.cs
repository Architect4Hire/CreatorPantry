using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ingredients.Data;
internal sealed class IngredientDataLayer(IIngredientRepository ingredients) : IIngredientDataLayer
{
    public Task<(IReadOnlyList<IngredientRecord> Rows, bool HasMore)> ListIngredientsAsync(
        IngredientQuery query, CancellationToken cancellationToken) =>
        ingredients.ListAsync(query, cancellationToken);

    public Task<IReadOnlyList<IngredientMatchIndexEntry>> LoadMatchIndexAsync(CancellationToken cancellationToken) =>
        ingredients.ListMatchIndexAsync(cancellationToken);

    public Task<bool> IsUsableAsync(Guid ingredientId, CancellationToken cancellationToken) =>
        ingredients.IsUsableAsync(ingredientId, cancellationToken);

    public Task<IReadOnlyList<IngredientAllergenTraitCounts>> CountAllergenTraitsAsync(
        IReadOnlyCollection<Guid> ingredientIds, CancellationToken cancellationToken) =>
        ingredients.CountAllergenTraitsAsync(ingredientIds, cancellationToken);

    public Task<CreatorPantry.Domain.Modules.Ingredients.Data.Entities.Ingredient?> FindActiveIngredientAsync(
        Guid ingredientId, CancellationToken cancellationToken) =>
        ingredients.FindActiveAsync(ingredientId, cancellationToken);

    public Task<Guid?> FindActiveReferenceSourceIdAsync(string code, CancellationToken cancellationToken) =>
        ingredients.FindActiveReferenceSourceIdAsync(code, cancellationToken);

    public Task<IReadOnlyList<IngredientCategoryServiceModel>> ListCategoriesAsync(
        IReadOnlyCollection<Guid> ingredientIds, CancellationToken cancellationToken) =>
        ingredients.ListCategoriesAsync(ingredientIds, cancellationToken);

    public Task<(IReadOnlyList<ApprovedAllergenTraitServiceModel> Allergens, IReadOnlyList<ApprovedDietaryTraitServiceModel> Dietary)> ListApprovedTraitsAsync(
        IReadOnlyCollection<Guid> ingredientIds,
        IReadOnlyCollection<Guid> allergenIds,
        IReadOnlyCollection<Guid> dietaryProfileIds,
        DateOnly asOf,
        CancellationToken cancellationToken) =>
        ingredients.ListApprovedTraitsAsync(ingredientIds, allergenIds, dietaryProfileIds, asOf, cancellationToken);
}
