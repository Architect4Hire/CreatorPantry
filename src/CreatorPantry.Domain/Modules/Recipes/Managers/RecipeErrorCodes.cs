namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>Stable error codes for recipe routes. Renaming a code is a breaking API change.</summary>
public static class RecipeErrorCodes
{
    /// <summary>The request body failed shape or format validation.</summary>
    public const string RecipeInvalidRequest = "recipes.recipe.invalid_request";

    /// <summary>The caller is a member of the workspace but their role does not permit this.</summary>
    /// <remarks>
    /// Distinct from <see cref="RecipeNotFound"/> on purpose. Being refused for role is safe to disclose to
    /// someone already known to belong here; it is not existence disclosure, because they can already see
    /// that the workspace is real.
    /// </remarks>
    public const string RecipeForbidden = "recipes.recipe.forbidden";

    /// <summary>
    /// Unknown recipe, or one belonging to another workspace: deliberately indistinguishable, so that a
    /// caller cannot learn a recipe exists by being refused it (tenancy.md).
    /// </summary>
    public const string RecipeNotFound = "recipes.recipe.not_found";

    /// <summary>
    /// The edit quoted a concurrency token the recipe has moved past: someone else saved first, or this
    /// caller composed the edit against a stale read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>.conflict</c> suffix is what <c>ProblemResults.StatusFor</c> maps to 409, and 409 is the
    /// honest status — the request is well formed and permitted, and would be accepted against the state the
    /// caller thought they had.
    /// </para>
    /// <para>
    /// The problem body deliberately carries no current token and no server content. The creator's attempted
    /// edit never left their editor, so nothing is lost, and a read returns both the state they are missing
    /// and a fresh token. Returning that state through an error path would mean widening
    /// <c>OperationError</c> for every code in the system so that one of them could answer a question a
    /// <c>GET</c> already answers.
    /// </para>
    /// </remarks>
    public const string RecipeConflict = "recipes.recipe.conflict";
}
