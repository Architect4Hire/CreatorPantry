using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Nutrition.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Nutrition.Data;

/// <summary>
/// Reads and stages nutrition mappings. Every query runs under the workspace query filter: a mapping of another
/// workspace is not found, never refused.
/// </summary>
public interface INutritionMappingRepository
{
    /// <summary>The mapping for one ingredient, tracked, with its rejections — for a change.</summary>
    Task<NutritionMapping?> FindForUpdateAsync(Guid ingredientId, CancellationToken cancellationToken);

    /// <summary>The mapping for one ingredient, untracked, with its rejections — for reading.</summary>
    Task<NutritionMapping?> FindAsync(Guid ingredientId, CancellationToken cancellationToken);

    /// <summary>The mappings among the ingredients named, untracked. An ingredient with none is simply absent.</summary>
    Task<IReadOnlyList<NutritionMapping>> ListAsync(IReadOnlyCollection<Guid> ingredientIds, CancellationToken cancellationToken);

    void Add(NutritionMapping mapping);
}

internal sealed class NutritionMappingRepository(CreatorPantryDbContext context) : INutritionMappingRepository
{
    public Task<NutritionMapping?> FindForUpdateAsync(Guid ingredientId, CancellationToken cancellationToken) =>
        context.NutritionMappings
            .Include(mapping => mapping.Rejections)
            .FirstOrDefaultAsync(mapping => mapping.IngredientId == ingredientId, cancellationToken);

    public Task<NutritionMapping?> FindAsync(Guid ingredientId, CancellationToken cancellationToken) =>
        context.NutritionMappings
            .AsNoTracking()
            .Include(mapping => mapping.Rejections)
            .FirstOrDefaultAsync(mapping => mapping.IngredientId == ingredientId, cancellationToken);

    public async Task<IReadOnlyList<NutritionMapping>> ListAsync(
        IReadOnlyCollection<Guid> ingredientIds, CancellationToken cancellationToken)
    {
        if (ingredientIds.Count == 0)
        {
            return [];
        }

        return await context.NutritionMappings
            .AsNoTracking()
            .Include(mapping => mapping.Rejections)
            .Where(mapping => ingredientIds.Contains(mapping.IngredientId))
            .ToListAsync(cancellationToken);
    }

    public void Add(NutritionMapping mapping) => context.NutritionMappings.Add(mapping);
}
