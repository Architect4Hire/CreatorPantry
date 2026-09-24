using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Recipes.Data;

internal sealed class RecipeRepository(CreatorPantryDbContext context) : IRecipeRepository
{
    public void Add(Recipe recipe) => context.Recipes.Add(recipe);

    public Task<CompleteRecipe?> GetCompleteAsync(Guid recipeId, CancellationToken cancellationToken) =>
        LoadAsync(recipeId, tracked: false, cancellationToken);

    public Task<CompleteRecipe?> GetForUpdateAsync(Guid recipeId, CancellationToken cancellationToken) =>
        LoadAsync(recipeId, tracked: true, cancellationToken);

    public Task<byte[]?> FindRowVersionAsync(Guid recipeId, CancellationToken cancellationToken) =>
        context.Recipes
            // No tracking, deliberately: this is asked after a save has failed, while the tracker still
            // holds the aggregate that failed to save. Materialising the row would hand back that tracked
            // instance and its stale token — the opposite of what the caller is asking for.
            .AsNoTracking()
            .Where(recipe => recipe.Id == recipeId)
            .Select(recipe => recipe.RowVersion)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<bool> ExistsAsync(Guid recipeId, CancellationToken cancellationToken) =>
        context.Recipes
            // No WorkspaceId predicate, and no IgnoreQueryFilters: the global query filter is what makes an
            // invisible recipe answer false here, which is the whole point of asking.
            .AsNoTracking()
            .AnyAsync(recipe => recipe.Id == recipeId, cancellationToken);

    /// <summary>
    /// The one query both reads use, because a snapshot taken from a half-loaded aggregate is a permanent
    /// mistake and two queries that had to stay identical would eventually not be.
    /// </summary>
    private async Task<CompleteRecipe?> LoadAsync(Guid recipeId, bool tracked, CancellationToken cancellationToken)
    {
        var recipe = await WholeAggregate(tracked)
            // No WorkspaceId predicate: the global query filter already scopes this, and adding one by hand
            // would suggest the filter is optional.
            .SingleOrDefaultAsync(candidate => candidate.Id == recipeId, cancellationToken);

        if (recipe is null)
        {
            return null;
        }

        var currentVersion = await context.RecipeVersions
            // Never tracked, even for the update path. Versions are immutable, so the only thing tracking
            // them could add is the possibility of modifying one.
            .AsNoTracking()
            .Where(version => version.RecipeId == recipeId)
            // By number rather than by CreatedAt: the number is the unique, gap-free identity creators cite,
            // and two versions written in the same tick would make a timestamp ordering arbitrary.
            .OrderByDescending(version => version.VersionNumber)
            // Deliberately no Include of Snapshot. The archive lives in its own table precisely so that
            // reading a recipe does not read every byte of its history.
            .FirstOrDefaultAsync(cancellationToken);

        return new CompleteRecipe(recipe, currentVersion);
    }

    private IQueryable<Recipe> WholeAggregate(bool tracked)
    {
        var recipes = tracked ? context.Recipes : context.Recipes.AsNoTracking();

        return recipes
            // Not a tuning preference — a correctness-of-scale requirement. Seven collections hang off this
            // root, and a single query multiplies them together: a recipe with 30 ingredients, 20 steps and
            // five each of equipment, assets and tags would return 30 × 20 × 5 × 5 × 5 = 375,000 rows to
            // fetch one recipe. Split querying costs eight small indexed round trips instead.
            .AsSplitQuery()
            // Ordering belongs in the query. Each SortOrder has a unique index leading with the parent, so
            // these are index reads, and a caller should never have to re-sort what the database can return
            // sorted. Tags carry no order because they are a set.
            .Include(candidate => candidate.IngredientGroups.OrderBy(group => group.SortOrder))
                .ThenInclude(group => group.Ingredients.OrderBy(ingredient => ingredient.SortOrder))
            .Include(candidate => candidate.InstructionGroups.OrderBy(group => group.SortOrder))
                .ThenInclude(group => group.Steps.OrderBy(step => step.SortOrder))
            .Include(candidate => candidate.Equipment.OrderBy(equipment => equipment.SortOrder))
            .Include(candidate => candidate.AssetLinks.OrderBy(link => link.SortOrder))
            .Include(candidate => candidate.Tags);
    }
}
