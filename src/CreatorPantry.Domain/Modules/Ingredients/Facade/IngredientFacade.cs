using CreatorPantry.Domain.Modules.Ingredients.Business;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ingredients.Data;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ingredients.Facade;
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

