using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Data;

public interface IRecipeExportDataLayer
{
    /// <summary>
    /// Reads the archived content an export describes: a named version, or the current one when none is named.
    /// </summary>
    /// <returns>
    /// Whether the recipe is visible in the resolved workspace, and the version read when one was found. An
    /// invisible recipe and a visible one with no such version are different facts a caller tells apart.
    /// </returns>
    Task<(bool RecipeVisible, RecipeVersionSnapshotRecord? Source)> FindSourceAsync(
        Guid recipeId, int? versionNumber, CancellationToken cancellationToken);
}

internal sealed class RecipeExportDataLayer(
    IRecipeRepository recipes, IRecipeVersionRepository versions) : IRecipeExportDataLayer
{
    // A read of immutable rows: there is no transaction to own, and an export is a projection nobody
    // benefits from caching at the cost of a stale answer.
    public async Task<(bool RecipeVisible, RecipeVersionSnapshotRecord? Source)> FindSourceAsync(
        Guid recipeId, int? versionNumber, CancellationToken cancellationToken)
    {
        if (!await recipes.ExistsAsync(recipeId, cancellationToken))
        {
            return (false, null);
        }

        var source = versionNumber is { } number
            ? await versions.FindSnapshotAsync(recipeId, number, cancellationToken)
            : await versions.FindCurrentSnapshotAsync(recipeId, cancellationToken);

        return (true, source);
    }
}
