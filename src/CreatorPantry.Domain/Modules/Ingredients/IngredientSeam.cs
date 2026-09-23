using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ingredients.Data;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ingredients;

/// <summary>
/// Application boundary for reading the shared ingredient catalogue.
/// </summary>
/// <remarks>
/// Global and read-only. No workspace, no user, no membership — the platform data zone (tenancy.md).
/// </remarks>
public interface IIngredientFacade
{
    Task<OperationResult<CursorPageServiceModel<IngredientServiceModel>>> ListIngredientsAsync(
        IngredientQueryViewModel model, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IIngredientFacade"/>
public interface IIngredientBusiness
{
    Task<CursorPageServiceModel<IngredientServiceModel>> ListIngredientsAsync(
        IngredientQuery query, CancellationToken cancellationToken);
}

/// <inheritdoc cref="Measurement.IMeasurementDataLayer"/>
public interface IIngredientDataLayer
{
    Task<(IReadOnlyList<IngredientRecord> Rows, bool HasMore)> ListIngredientsAsync(
        IngredientQuery query, CancellationToken cancellationToken);
}

internal sealed class IngredientFacade(
    IValidator<IngredientQueryViewModel> validator,
    IIngredientBusiness business,
    CachedPageReader reader) : IIngredientFacade
{
    /// <summary>The cache-key resource literal. Unchanged by the module split, so cached keys stay identical.</summary>
    private const string Resource = "ingredients";

    public Task<OperationResult<CursorPageServiceModel<IngredientServiceModel>>> ListIngredientsAsync(
        IngredientQueryViewModel model, CancellationToken cancellationToken) =>
        reader.ReadAsync<IngredientQueryViewModel, IngredientQuery, IngredientServiceModel>(
            validator,
            model,
            Resource,
            IngredientQueryFactory.TryCreate,
            query => query.CacheKeySegment,
            business.ListIngredientsAsync,
            cancellationToken);
}

internal sealed class IngredientBusiness(IIngredientDataLayer dataLayer) : IIngredientBusiness
{
    public async Task<CursorPageServiceModel<IngredientServiceModel>> ListIngredientsAsync(
        IngredientQuery query, CancellationToken cancellationToken)
    {
        var (rows, hasMore) = await dataLayer.ListIngredientsAsync(query, cancellationToken);

        return PageBuilder.Build(rows, hasMore, query.Scope, row => new IngredientServiceModel(
            row.Id, row.CanonicalName, row.FoodCategoryCode, row.DefaultCountUnitCode, row.Aliases));
    }
}

internal sealed class IngredientDataLayer(IIngredientRepository ingredients) : IIngredientDataLayer
{
    public Task<(IReadOnlyList<IngredientRecord> Rows, bool HasMore)> ListIngredientsAsync(
        IngredientQuery query, CancellationToken cancellationToken) =>
        ingredients.ListAsync(query, cancellationToken);
}
