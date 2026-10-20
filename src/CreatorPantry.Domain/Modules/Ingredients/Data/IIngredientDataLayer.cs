using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ingredients.Data;
/// <inheritdoc cref="CreatorPantry.Domain.Modules.Measurement.Data.IMeasurementDataLayer"/>
public interface IIngredientDataLayer
{
    Task<(IReadOnlyList<IngredientRecord> Rows, bool HasMore)> ListIngredientsAsync(
        IngredientQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyList<IngredientMatchIndexEntry>> LoadMatchIndexAsync(CancellationToken cancellationToken);

    /// <inheritdoc cref="IIngredientRepository.IsUsableAsync"/>
    Task<bool> IsUsableAsync(Guid ingredientId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IIngredientRepository.CountAllergenTraitsAsync"/>
    Task<IReadOnlyList<IngredientAllergenTraitCounts>> CountAllergenTraitsAsync(
        IReadOnlyCollection<Guid> ingredientIds, CancellationToken cancellationToken);

    /// <inheritdoc cref="IIngredientRepository.FindActiveAsync"/>
    Task<CreatorPantry.Domain.Modules.Ingredients.Data.Entities.Ingredient?> FindActiveIngredientAsync(
        Guid ingredientId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IIngredientRepository.FindActiveReferenceSourceIdAsync"/>
    Task<Guid?> FindActiveReferenceSourceIdAsync(string code, CancellationToken cancellationToken);

    /// <inheritdoc cref="IIngredientRepository.ListCategoriesAsync"/>
    Task<IReadOnlyList<IngredientCategoryServiceModel>> ListCategoriesAsync(
        IReadOnlyCollection<Guid> ingredientIds, CancellationToken cancellationToken);

    /// <inheritdoc cref="IIngredientRepository.ListApprovedTraitsAsync"/>
    Task<(IReadOnlyList<ApprovedAllergenTraitServiceModel> Allergens, IReadOnlyList<ApprovedDietaryTraitServiceModel> Dietary)> ListApprovedTraitsAsync(
        IReadOnlyCollection<Guid> ingredientIds,
        IReadOnlyCollection<Guid> allergenIds,
        IReadOnlyCollection<Guid> dietaryProfileIds,
        DateOnly asOf,
        CancellationToken cancellationToken);
}

