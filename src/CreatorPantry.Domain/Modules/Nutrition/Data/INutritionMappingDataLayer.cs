using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Nutrition.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Nutrition.Data;

/// <summary>How one write of a mapping ended.</summary>
public enum NutritionMappingWrite
{
    Saved = 0,

    /// <summary>
    /// The mapping changed or went since it was read — or, for a new one, another request made it first. Nothing
    /// was written.
    /// </summary>
    Stale = 1,

    /// <summary>The store refused the write. Nothing was written.</summary>
    Refused = 2,
}

/// <summary>Persistence for nutrition mappings: one save per change, with its audit entry in the same commit.</summary>
public interface INutritionMappingDataLayer
{
    /// <inheritdoc cref="INutritionMappingRepository.FindForUpdateAsync"/>
    Task<NutritionMapping?> FindForUpdateAsync(Guid ingredientId, CancellationToken cancellationToken);

    /// <inheritdoc cref="INutritionMappingRepository.FindAsync"/>
    Task<NutritionMapping?> FindAsync(Guid ingredientId, CancellationToken cancellationToken);

    /// <inheritdoc cref="INutritionMappingRepository.ListAsync"/>
    Task<IReadOnlyList<NutritionMapping>> ListAsync(IReadOnlyCollection<Guid> ingredientIds, CancellationToken cancellationToken);

    /// <summary>
    /// Saves <paramref name="mapping"/> — added when <paramref name="isNew"/>, otherwise the tracked row with its
    /// rejections — and <paramref name="audit"/> with it, in one commit.
    /// </summary>
    /// <remarks>
    /// The row's UPDATE carries the row version it was read with, so a change that lost a race to another is
    /// <see cref="NutritionMappingWrite.Stale"/>. A new mapping that lost the race to create one is too: the
    /// unique (workspace, ingredient) index refuses the second, and the caller is told to read again.
    /// </remarks>
    Task<NutritionMappingWrite> SaveAsync(
        NutritionMapping mapping, bool isNew, AuditEntry? audit, CancellationToken cancellationToken);
}

internal sealed class NutritionMappingDataLayer(
    INutritionMappingRepository repository,
    CreatorPantryDbContext context,
    IAuditWriter auditWriter) : INutritionMappingDataLayer
{
    public Task<NutritionMapping?> FindForUpdateAsync(Guid ingredientId, CancellationToken cancellationToken) =>
        repository.FindForUpdateAsync(ingredientId, cancellationToken);

    public Task<NutritionMapping?> FindAsync(Guid ingredientId, CancellationToken cancellationToken) =>
        repository.FindAsync(ingredientId, cancellationToken);

    public Task<IReadOnlyList<NutritionMapping>> ListAsync(
        IReadOnlyCollection<Guid> ingredientIds, CancellationToken cancellationToken) =>
        repository.ListAsync(ingredientIds, cancellationToken);

    public async Task<NutritionMappingWrite> SaveAsync(
        NutritionMapping mapping, bool isNew, AuditEntry? audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        if (isNew)
        {
            repository.Add(mapping);
        }

        if (audit is not null)
        {
            // Staged, not saved: the audit row commits with the change it records, or neither does.
            auditWriter.Record(audit);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return NutritionMappingWrite.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            context.ChangeTracker.Clear();
            return NutritionMappingWrite.Stale;
        }
        catch (DbUpdateException)
        {
            // A new mapping refused because one now exists lost the race to make it: the unique index kept the
            // ingredient to one decision, and the caller reads that one. Anything else is a refusal.
            context.ChangeTracker.Clear();

            return isNew && await repository.FindAsync(mapping.IngredientId, cancellationToken) is not null
                ? NutritionMappingWrite.Stale
                : NutritionMappingWrite.Refused;
        }
    }
}
