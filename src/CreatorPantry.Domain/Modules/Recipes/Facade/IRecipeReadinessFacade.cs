using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ingredients.Facade;
using CreatorPantry.Domain.Modules.Recipes.Business;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Facade;

/// <summary>
/// The application boundary for "is this recipe ready" (TESTRUN-004).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A facade of its own, and not by preference.</strong> <c>AiProposalBusiness</c> already depends on
/// <see cref="IRecipeFacade"/>, so a readiness method added to that interface — which must reach
/// <see cref="IAiProposalFacade"/> — would close a constructor cycle through the container and fail at resolution
/// rather than at review. Keeping this in its own class means the two dependencies run in opposite directions
/// between different types, which the container resolves without difficulty.
/// </para>
/// <para>
/// It is also the coherent boundary on its own terms: readiness is a question with its own rules, its own route and
/// its own configuration, and <see cref="IRecipeFacade"/> already carries every recipe operation there is.
/// </para>
/// <para>
/// <strong>This is a read and changes nothing.</strong> No idempotency key, no role beyond membership, no audit
/// event — TESTRUN-004 writes nothing, and a facade that recorded having evaluated would be the first thing to
/// break that.
/// </para>
/// </remarks>
public interface IRecipeReadinessFacade
{
    /// <summary>
    /// Evaluates one recipe against the readiness rules and returns every rule's verdict.
    /// </summary>
    /// <param name="recipeId">The recipe to evaluate, from the route.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The evaluation, or <c>recipes.recipe.not_found</c> for a recipe the caller may not see — an unknown recipe
    /// and another workspace's answer identically.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The result describes the recipe at the moment it was read and carries the version and concurrency token that
    /// fix which moment that was. It is <strong>not a status</strong> and must not be stored as one; see
    /// <see cref="RecipeReadinessServiceModel"/>.
    /// </para>
    /// <para>
    /// <strong>It is not a safety, allergen, nutrition or dietary clearance</strong>, and no combination of
    /// findings makes it one.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeReadinessServiceModel>> EvaluateAsync(
        Guid recipeId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRecipeReadinessFacade"/>
internal sealed class RecipeReadinessFacade(
    IRecipeReadinessBusiness business,
    IAiProposalFacade proposals,
    IIngredientFacade ingredients) : IRecipeReadinessFacade
{
    public async Task<OperationResult<RecipeReadinessServiceModel>> EvaluateAsync(
        Guid recipeId,
        CancellationToken cancellationToken)
    {
        // The facts first, and the recipe's visibility with them. Asking the other two modules about a recipe the
        // caller may not see would be work done on behalf of somebody who gets a 404 — and, for the AI read, a
        // question about a recipe this caller has no business naming.
        var read = await business.FindFactsAsync(recipeId, cancellationToken);

        if (!read.Succeeded)
        {
            return OperationResult<RecipeReadinessServiceModel>.Failure(read.Error!);
        }

        var facts = read.Value!;

        // Facade to facade, which is the only way across a module boundary — and the reason the evaluation is split
        // in two. Business may not make either of these calls, and neither could be made before the facts were
        // read, because the allergen question is about the ingredient ids the lines turned out to carry.
        var ai = await proposals.SummarizeOutstandingAsync(recipeId, cancellationToken);

        var matchedIngredientIds = facts.IngredientLines
            .Select(line => line.IngredientId)
            .OfType<Guid>()
            .Distinct()
            .ToList();

        // Skipped entirely when no line resolved to anything: a read of nothing is a read nobody needs, and the
        // unrecognised-lines rule is already reporting that situation in its own words.
        var allergenGaps = matchedIngredientIds.Count is 0
            ? []
            : await ingredients.FindAllergenReviewGapsAsync(matchedIngredientIds, cancellationToken);

        return OperationResult<RecipeReadinessServiceModel>.Success(
            business.Evaluate(facts, new RecipeReadinessExternalFacts(ai, allergenGaps)));
    }
}
