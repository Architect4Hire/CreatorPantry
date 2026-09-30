using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Domain.Modules.Recipes.Business;

/// <summary>
/// The domain rules behind "is this recipe ready", in two halves: reading the facts, and judging them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two methods rather than one, and the split is forced rather than stylistic.</strong> Four of the twenty
/// rules read facts that belong to other modules, and the allergen question can only be asked once this module
/// knows which vocabulary entries its lines resolved to. So the facts are read first, the facade spends the
/// ingredient ids it finds in them on the two cross-module reads, and the judging happens afterwards with
/// everything in hand. A single method would mean either Business calling another module's facade — which
/// backend.md forbids — or the facade guessing which ingredients to ask about before anything has read them.
/// </para>
/// <para>
/// <strong>Nothing here writes.</strong> No audit, no cache, no state change, no clock: TESTRUN-004 is a read, and
/// a readiness verdict stored anywhere would be a second source of truth that goes stale on the next edit.
/// </para>
/// </remarks>
public interface IRecipeReadinessBusiness
{
    /// <summary>
    /// Reads every fact this module holds about one recipe's readiness.
    /// </summary>
    /// <returns>
    /// The facts, or <c>recipes.recipe.not_found</c> when the recipe is not visible in the resolved workspace — an
    /// unknown recipe and another workspace's answer identically (tenancy.md).
    /// </returns>
    Task<OperationResult<RecipeReadinessFacts>> FindFactsAsync(Guid recipeId, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the rules over facts already read.
    /// </summary>
    /// <param name="facts">From <see cref="FindFactsAsync"/>.</param>
    /// <param name="external">
    /// What the other modules said, gathered by the facade. <see cref="RecipeReadinessExternalFacts.None"/> is the
    /// honest value when neither was asked.
    /// </param>
    /// <remarks>
    /// <para>
    /// Synchronous, because it performs no I/O — and that signature is the guarantee, not a comment. It is on this
    /// interface rather than called straight from the facade because the configured severities are a domain
    /// decision this layer owns; a facade reaching for <see cref="RecipeReadinessEvaluator"/> itself would be a
    /// controller-shaped shortcut one layer up.
    /// </para>
    /// <para>
    /// No cancellation token, for the same reason there is no <c>async</c>: there is nothing to cancel.
    /// </para>
    /// </remarks>
    RecipeReadinessServiceModel Evaluate(RecipeReadinessFacts facts, RecipeReadinessExternalFacts external);
}

internal sealed class RecipeReadinessBusiness(
    IRecipeReadinessDataLayer dataLayer,
    IOptions<RecipeReadinessOptions> options) : IRecipeReadinessBusiness
{
    public async Task<OperationResult<RecipeReadinessFacts>> FindFactsAsync(
        Guid recipeId,
        CancellationToken cancellationToken)
    {
        var facts = await dataLayer.FindFactsAsync(recipeId, cancellationToken);

        if (facts is null)
        {
            // The recipe is not visible. Answered identically to one that was never created, because the layer
            // beneath cannot tell them apart and this one must not be able to either (tenancy.md).
            return OperationResult<RecipeReadinessFacts>.Failure(new OperationError(
                RecipeErrorCodes.RecipeNotFound,
                "That recipe could not be found.",
                new Dictionary<string, string[]>()));
        }

        return OperationResult<RecipeReadinessFacts>.Success(facts);
    }

    // Read once per evaluation rather than captured at construction: IOptions<T> resolves the current value, and a
    // deployment that changes a severity should take effect on the next request rather than the next restart.
    public RecipeReadinessServiceModel Evaluate(
        RecipeReadinessFacts facts, RecipeReadinessExternalFacts external) =>
        RecipeReadinessEvaluator.Evaluate(facts, external, options.Value);
}
