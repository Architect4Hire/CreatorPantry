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
}
