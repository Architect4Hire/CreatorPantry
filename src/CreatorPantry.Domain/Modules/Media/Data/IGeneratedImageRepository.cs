using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Media.Data;

/// <summary>
/// EF Core access to the generated-image workspace, within the resolved workspace.
/// </summary>
/// <remarks>
/// One method, for the one operation that exists: resolving an image another module is about to reference.
/// Staging, retrieval and the retention sweep each bring their own query when their prompts land (12.7, 12.8),
/// rather than a general-purpose read being provided here in advance of a caller.
/// </remarks>
public interface IGeneratedImageRepository
{
    /// <summary>Whether this workspace holds a generated image with that id.</summary>
    /// <remarks>
    /// False for an id this workspace has never held and for another workspace's image — one answer, because
    /// the global query filter means the neighbour's row is not in the set this reads over, and because a
    /// caller only needs to know whether it may store the value (tenancy.md).
    /// </remarks>
    Task<bool> ExistsAsync(Guid generatedImageId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether this workspace holds that image and it is still one a creator can work from: staged or kept,
    /// and not declined or expired.
    /// </summary>
    /// <remarks>False for an unknown id and for another workspace's image, exactly as <see cref="ExistsAsync"/>.</remarks>
    Task<bool> IsAvailableAsync(Guid generatedImageId, CancellationToken cancellationToken);

    /// <summary>The image, tracked, or null when this workspace holds none with that id.</summary>
    /// <remarks>
    /// No <c>WorkspaceId</c> predicate and no <c>IgnoreQueryFilters</c>: the key seek happens inside the
    /// filtered set, so a neighbour's image is <em>not found</em> rather than found and refused. That is
    /// what makes "unknown id" and "someone else's id" one answer at the HTTP boundary (tenancy.md).
    /// </remarks>
    Task<GeneratedImage?> FindAsync(Guid generatedImageId, CancellationToken cancellationToken);

    /// <summary>Which of <paramref name="objectKeys"/> this workspace holds an image for.</summary>
    /// <remarks>
    /// The question orphan reconciliation asks, asked about a whole page at once: a key with no row
    /// behind it is bytes nothing will ever read, and a key with one is an image somebody may be about
    /// to look at. One query per page rather than one per object, because a sweep that walks a whole
    /// prefix would otherwise make a round trip for every image a creator has.
    /// </remarks>
    Task<IReadOnlySet<string>> FindOwnedObjectKeysAsync(
        IReadOnlyCollection<string> objectKeys, CancellationToken cancellationToken);

    /// <summary>Staged images of this workspace whose retention deadline has passed.</summary>
    Task<IReadOnlyList<GeneratedImage>> FindExpiredAsync(
        DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>Images of this workspace whose staging bytes are no longer needed.</summary>
    /// <remarks>
    /// <para>
    /// Kept as well as rejected and expired, since 12.9a: a kept image's bytes were copied into a DAM
    /// asset that now owns them, so the staging copy is redundant.
    /// </para>
    /// <para>
    /// <strong>A kept image is only purgeable once a version actually names it.</strong> In practice
    /// <c>Kept</c> is set by nothing but a committed asset creation, so the two always go together — but
    /// "nothing is removed until something else demonstrably holds it" should be a query rather than a
    /// convention. Anything that set the status without copying would otherwise have the sweep delete a
    /// creator's only copy.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<GeneratedImage>> FindPurgeableAsync(int limit, CancellationToken cancellationToken);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

}

/// <inheritdoc cref="IGeneratedImageRepository"/>
internal sealed class GeneratedImageRepository(CreatorPantryDbContext context) : IGeneratedImageRepository
{
    public Task<bool> ExistsAsync(Guid generatedImageId, CancellationToken cancellationToken) =>
        context.GeneratedImages
            .AsNoTracking()

            // No WorkspaceId predicate: the key seek happens inside the filtered set, so another workspace's
            // image is not found rather than found and rejected.
            .AnyAsync(image => image.Id == generatedImageId, cancellationToken);

    public Task<bool> IsAvailableAsync(Guid generatedImageId, CancellationToken cancellationToken) =>
        context.GeneratedImages
            .AsNoTracking()

            // Inside the filtered set, as above.
            .AnyAsync(
                image => image.Id == generatedImageId
                    && (image.Status == GeneratedImageStatus.Staged || image.Status == GeneratedImageStatus.Kept),
                cancellationToken);

    public Task<GeneratedImage?> FindAsync(Guid generatedImageId, CancellationToken cancellationToken) =>
        context.GeneratedImages.FirstOrDefaultAsync(
            image => image.Id == generatedImageId, cancellationToken);

    public async Task<IReadOnlySet<string>> FindOwnedObjectKeysAsync(
        IReadOnlyCollection<string> objectKeys, CancellationToken cancellationToken)
    {
        if (objectKeys.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var owned = await context.GeneratedImages
            .AsNoTracking()
            .Where(image => objectKeys.Contains(image.ObjectKey))
            .Select(image => image.ObjectKey)
            .ToListAsync(cancellationToken);

        return owned.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<GeneratedImage>> FindExpiredAsync(
        DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        await context.GeneratedImages
            .Where(image => image.Status == GeneratedImageStatus.Staged && image.RetentionExpiresAt <= now)
            .OrderBy(image => image.RetentionExpiresAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<GeneratedImage>> FindPurgeableAsync(
        int limit, CancellationToken cancellationToken) =>
        await context.GeneratedImages
            .Where(image => (image.Status == GeneratedImageStatus.Rejected
                    || image.Status == GeneratedImageStatus.Expired
                    || (image.Status == GeneratedImageStatus.Kept
                        && context.MediaAssetVersions.Any(
                            version => version.SourceGeneratedImageId == image.Id)))
                && image.ObjectDeletedAt == null)
            .OrderBy(image => image.StatusChangedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        context.SaveChangesAsync(cancellationToken);
}
