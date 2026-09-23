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
}

