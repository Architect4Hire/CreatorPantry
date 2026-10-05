using CreatorPantry.Domain.Managers.Persistence;
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
}
