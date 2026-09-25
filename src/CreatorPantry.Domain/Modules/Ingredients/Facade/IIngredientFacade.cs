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

    /// <summary>
    /// Matches each candidate string against the ingredient catalogue by exact/alias normalization, ranked by
    /// precedence with unresolved ambiguity called out rather than guessed (ING-001, AIREC-GR-003).
    /// </summary>
    /// <remarks>
    /// Read-only and global: no workspace context is required or accepted, matching every other read in this
    /// module. Results preserve the order of <paramref name="candidateTexts"/>.
    /// </remarks>
    Task<OperationResult<IReadOnlyList<IngredientMatchResult>>> ResolveCandidatesAsync(
        IReadOnlyList<string> candidateTexts, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the id names an active ingredient — the same question <c>IVocabularyFacade.IsUsableAsync</c>
    /// answers for the four controlled vocabularies, asked of this module's own catalogue instead. Used by
    /// another module's write seam (a recipe's ingredient lines, say) to verify a submitted reference before
    /// it reaches a foreign key.
    /// </summary>
    Task<bool> IsUsableAsync(Guid ingredientId, CancellationToken cancellationToken);
}

