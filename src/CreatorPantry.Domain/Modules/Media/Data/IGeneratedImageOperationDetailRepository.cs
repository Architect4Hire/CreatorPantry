using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Media.Data;

/// <summary>
/// EF Core access to one image-generation operation and its images, within the resolved workspace.
/// </summary>
public interface IGeneratedImageOperationDetailRepository
{
    /// <summary>
    /// One operation with its images, or null when this workspace has no operation with that id.
    /// </summary>
    Task<GeneratedImageOperationDetailServiceModel?> FindAsync(
        Guid operationId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGeneratedImageOperationDetailRepository"/>
/// <remarks>
/// <para>
/// <strong>The workspace is the global query filter, never a predicate here.</strong> An unknown id and another
/// workspace's operation both arrive as null, which is what lets the layer above answer 404 to both without
/// disclosing which it was (tenancy.md).
/// </para>
/// <para>
/// <strong>Projections throughout, so no object key or checksum is ever loaded.</strong> Nothing here
/// materialises a <c>GeneratedImage</c> or a <c>GeneratedImageOperation</c>; the image rows select nine columns
/// and <c>ObjectKey</c> is not among them, and neither is the operation's <c>PromptText</c>. That is stronger
/// than reading the entity and dropping the fields, because a later edit cannot reintroduce one by accident
/// (media.md) — the same discipline <c>MediaAssetDetailRepository</c> applies.
/// </para>
/// <para>
/// <strong>Two statements rather than one.</strong> The operation, then its images. A single query over the
/// collection would repeat the operation's columns once per image, and <c>AsSplitQuery</c> would do the same
/// thing less explicitly while still materialising entities.
/// </para>
/// <para>
/// Ordered by <c>VariantIndex</c> so a contact sheet keeps its order however the rows were inserted — a worker
/// stages variants as the provider returns them, which is not necessarily in order.
/// </para>
/// </remarks>
internal sealed class GeneratedImageOperationDetailRepository(CreatorPantryDbContext context)
    : IGeneratedImageOperationDetailRepository
{
    public async Task<GeneratedImageOperationDetailServiceModel?> FindAsync(
        Guid operationId, CancellationToken cancellationToken)
    {
        var operation = await context.GeneratedImageOperations
            .AsNoTracking()
            .Where(row => row.Id == operationId)
            .Select(row => new
            {
                row.Id,
                row.Status,
                row.VariantCount,
                row.ProviderName,
                row.ModelName,
                row.FailureCategory,
                row.FailureSummary,
                row.RequestedAt,
                row.CompletedAt,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (operation is null)
        {
            return null;
        }

        var images = await context.GeneratedImages
            .AsNoTracking()
            .Where(row => row.GeneratedImageOperationId == operationId)
            .OrderBy(row => row.VariantIndex)
            .Select(row => new StagedImageServiceModel(
                row.Id,
                row.VariantIndex,
                row.Status,
                row.MediaType,
                row.Width,
                row.Height,
                row.SizeBytes,
                row.RetentionExpiresAt,
                row.CreatedAt))
            .ToListAsync(cancellationToken);

        return new GeneratedImageOperationDetailServiceModel(
            operation.Id,
            operation.Status,
            operation.VariantCount,
            // A row exists only once there are bytes to describe, so the rows are the staged count.
            images.Count,
            operation.ProviderName,
            operation.ModelName,
            operation.FailureCategory,
            operation.FailureSummary,
            operation.RequestedAt,
            operation.CompletedAt,
            images);
    }
}
