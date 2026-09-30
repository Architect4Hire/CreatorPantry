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

    /// <summary>
    /// Which of the named ingredients have an incomplete recorded allergen picture, and in what way.
    /// </summary>
    /// <returns>
    /// One entry per ingredient that is not <see cref="IngredientAllergenReviewState.Reviewed"/>. An empty list
    /// means every one of them has been checked — which is a statement about the records and never a claim that
    /// the food is free of anything.
    /// </returns>
    /// <remarks>
    /// The precedence that turns three counts into one state lives here rather than in the query, because which
    /// gap matters most is a domain judgement — see <see cref="IngredientAllergenReviewState"/>. An id naming
    /// nothing, or an inactive ingredient, is reported as <see cref="IngredientAllergenReviewState.NoTraitsRecorded"/>
    /// rather than dropped: a caller holding a reference to an ingredient the catalogue will not vouch for has a
    /// gap, and silently omitting it would read as "checked".
    /// </remarks>
    Task<IReadOnlyList<IngredientAllergenReviewServiceModel>> FindAllergenReviewGapsAsync(
        IReadOnlyCollection<Guid> ingredientIds, CancellationToken cancellationToken);
}

