using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Facade;

/// <summary>
/// Combines the ingredient-line tokenizer (7.2) and the ingredient/unit reference matchers (7.3) into one
/// read-only proposal (ING-001, 7.4). Nothing here persists anything or resolves a workspace: both downstream
/// facades are global reference reads, and membership is already gated by the route's authorization policy.
/// </summary>
public interface IIngredientParsingFacade
{
    Task<OperationResult<ParseIngredientLinesResult>> ParseAsync(
        ParseIngredientLinesViewModel model, CancellationToken cancellationToken);
}
