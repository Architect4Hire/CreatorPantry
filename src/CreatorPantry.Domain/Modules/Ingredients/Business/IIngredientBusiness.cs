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

    /// <summary>Loads the flattened match index the facade caches and passes back into <see cref="ResolveCandidates"/>.</summary>
    Task<IReadOnlyList<IngredientMatchIndexEntry>> LoadMatchIndexAsync(CancellationToken cancellationToken);

    /// <summary>Resolves each candidate against an already-loaded index. Pure: no I/O, no caching decision.</summary>
    IReadOnlyList<IngredientMatchResult> ResolveCandidates(
        IReadOnlyList<string> candidateTexts, IReadOnlyList<IngredientMatchIndexEntry> index);

    /// <summary>Whether the id names an ingredient another module's writer may reference, such as a recipe line.</summary>
    Task<bool> IsUsableAsync(Guid ingredientId, CancellationToken cancellationToken);
}

