using CreatorPantry.Domain.Modules.Ingredients.Data;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ingredients.Business;
/// <inheritdoc cref="CreatorPantry.Domain.Modules.Ingredients.Facade.IIngredientFacade"/>
public interface IIngredientBusiness
{
    Task<CursorPageServiceModel<IngredientServiceModel>> ListIngredientsAsync(
        IngredientQuery query, CancellationToken cancellationToken);
}

