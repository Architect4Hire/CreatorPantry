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
}

