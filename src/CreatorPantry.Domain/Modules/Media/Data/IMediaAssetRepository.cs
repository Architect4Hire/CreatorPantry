using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CreatorPantry.Domain.Modules.Media.Data;

/// <summary>
/// EF Core access to the DAM, within the resolved workspace.
/// </summary>
/// <remarks>
/// Every read is filtered, so a neighbour's asset is <em>not found</em> rather than found and refused —
/// which is what makes "unknown id" and "someone else's id" one answer at the HTTP boundary (tenancy.md).
/// </remarks>
public interface IMediaAssetRepository
{
    /// <summary>Whether this workspace holds an asset with that id and has not deleted it.</summary>
    Task<bool> ExistsAsync(Guid mediaAssetId, CancellationToken cancellationToken);

    /// <summary>The asset and its versions, or null when this workspace holds none with that id.</summary>
    Task<MediaAsset?> FindAsync(Guid mediaAssetId, CancellationToken cancellationToken);

    /// <summary>
    /// The asset whose first version was made from this staged image, or null.
    /// </summary>
    /// <remarks>
    /// The natural replay guard for the generated path: a staged image is <c>Kept</c> exactly once, and a
    /// second attempt to keep it should hand back the asset the first one made rather than a second asset
    /// or an error. No idempotency header is needed for it to work.
    /// </remarks>
    Task<MediaAsset?> FindByGeneratedImageAsync(Guid generatedImageId, CancellationToken cancellationToken);

    /// <summary>The workspace tags among <paramref name="tagIds"/> that this workspace actually has.</summary>
    Task<IReadOnlyList<Guid>> FindTagIdsAsync(
        IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken);

    /// <summary>Whether this workspace holds a recipe with that id.</summary>
    Task<bool> RecipeExistsAsync(Guid recipeId, CancellationToken cancellationToken);

    /// <summary>The next sort order for a recipe's asset links, so a new link lands at the end.</summary>
    Task<int> NextRecipeLinkOrderAsync(Guid recipeId, CancellationToken cancellationToken);

    void Add(MediaAsset asset);

    void Add(MediaAssetVersion version);

    void Add(MediaAssetTag tag);

    void Add(RecipeAssetLink link);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Begins a transaction on this scope's context, or null when one is already open.</summary>
    /// <remarks>
    /// Null rather than a nested transaction, because the caller must not commit one it did not begin —
    /// an ambient transaction belongs to whoever opened it, which for a request carrying an
    /// <c>Idempotency-Key</c> is the idempotent executor.
    /// </remarks>
    Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMediaAssetRepository"/>
internal sealed class MediaAssetRepository(CreatorPantryDbContext context) : IMediaAssetRepository
{
    public Task<bool> ExistsAsync(Guid mediaAssetId, CancellationToken cancellationToken) =>
        context.MediaAssets
            .AsNoTracking()
            .AnyAsync(asset => asset.Id == mediaAssetId && asset.DeletedAt == null, cancellationToken);

    public Task<MediaAsset?> FindAsync(Guid mediaAssetId, CancellationToken cancellationToken) =>
        context.MediaAssets
            .Include(asset => asset.Versions)
            .FirstOrDefaultAsync(asset => asset.Id == mediaAssetId, cancellationToken);

    public async Task<MediaAsset?> FindByGeneratedImageAsync(
        Guid generatedImageId, CancellationToken cancellationToken)
    {
        var assetId = await context.MediaAssetVersions
            .AsNoTracking()
            .Where(version => version.SourceGeneratedImageId == generatedImageId)
            .Select(version => (Guid?)version.MediaAssetId)
            .FirstOrDefaultAsync(cancellationToken);

        return assetId is null ? null : await FindAsync(assetId.Value, cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> FindTagIdsAsync(
        IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken) =>
        tagIds.Count == 0
            ? []
            : await context.WorkspaceTags
                .AsNoTracking()
                .Where(tag => tagIds.Contains(tag.Id))
                .Select(tag => tag.Id)
                .ToListAsync(cancellationToken);

    public Task<bool> RecipeExistsAsync(Guid recipeId, CancellationToken cancellationToken) =>
        context.Recipes.AsNoTracking().AnyAsync(recipe => recipe.Id == recipeId, cancellationToken);

    public async Task<int> NextRecipeLinkOrderAsync(Guid recipeId, CancellationToken cancellationToken) =>
        await context.RecipeAssetLinks
            .AsNoTracking()
            .Where(link => link.RecipeId == recipeId)
            .Select(link => (int?)link.SortOrder)
            .MaxAsync(cancellationToken) + 1 ?? 0;

    public void Add(MediaAsset asset) => context.MediaAssets.Add(asset);

    public void Add(MediaAssetVersion version) => context.MediaAssetVersions.Add(version);

    public void Add(MediaAssetTag tag) => context.MediaAssetTags.Add(tag);

    public void Add(RecipeAssetLink link) => context.RecipeAssetLinks.Add(link);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        context.SaveChangesAsync(cancellationToken);

    public async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken) =>
        context.Database.CurrentTransaction is null
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;
}
