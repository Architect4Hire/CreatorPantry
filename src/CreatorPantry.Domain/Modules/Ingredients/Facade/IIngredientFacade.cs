using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ingredients.Data;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ingredients.Facade;
public interface IIngredientFacade
{
    Task<OperationResult<CursorPageServiceModel<IngredientServiceModel>>> ListIngredientsAsync(
        IngredientQueryViewModel model, CancellationToken cancellationToken);
}

