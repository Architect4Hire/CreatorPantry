using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Data;

/// <summary>
/// The complete read behind a readiness evaluation. Composes the repository; owns no transaction, because there is
/// nothing to commit.
/// </summary>
/// <remarks>
/// <para>
/// A layer with one method and no transaction looks thin, and it is still the right shape: the seam is
/// Controller → Facade → Business → DataLayer → Repository, and a Business that called the repository directly is
/// the defect backend.md names. What it buys concretely is the place a second read goes when a rule needs one —
/// the snapshot-derived rules that arrive with nutrition, say — without Business learning about repositories.
/// </para>
/// <para>
/// <strong>Nothing here writes</strong>, and the absence of an <c>IAuditWriter</c> and a <c>SaveChangesAsync</c> is
/// what makes that structural rather than a promise.
/// </para>
/// </remarks>
public interface IRecipeReadinessDataLayer
{
    /// <inheritdoc cref="IRecipeReadinessRepository.FindFactsAsync"/>
    Task<RecipeReadinessFacts?> FindFactsAsync(Guid recipeId, CancellationToken cancellationToken);
}

internal sealed class RecipeReadinessDataLayer(IRecipeReadinessRepository readiness) : IRecipeReadinessDataLayer
{
    public Task<RecipeReadinessFacts?> FindFactsAsync(Guid recipeId, CancellationToken cancellationToken) =>
        readiness.FindFactsAsync(recipeId, cancellationToken);
}
