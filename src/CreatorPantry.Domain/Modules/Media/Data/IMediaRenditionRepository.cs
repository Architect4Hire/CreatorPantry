using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Media.Data;

/// <summary>What a source's own row says about its stored bytes.</summary>
public sealed record MediaRenditionSourceFacts(string ObjectKey, string ContentChecksum, long SizeBytes);

/// <summary>
/// Persistence for <see cref="MediaRendition"/> rows. Every read is inside the workspace query filter.
/// </summary>
public interface IMediaRenditionRepository
{
    /// <summary>Every rendition row of one staged image, in purpose order.</summary>
    Task<IReadOnlyList<MediaRendition>> FindForGeneratedImageAsync(
        Guid generatedImageId, CancellationToken cancellationToken);

    /// <summary>
    /// The source's stored bytes as its row describes them, or null when it is not a picture renditions
    /// are made for: unknown, a staged image that was settled or whose bytes are gone, or a version of a
    /// deleted asset.
    /// </summary>
    /// <remarks>
    /// The same eligibility <c>MediaRenditionClaimRepository</c> uses to find work, on purpose. If the two
    /// disagreed, a picture could be claimed on every pass and refused on every delivery.
    /// </remarks>
    Task<MediaRenditionSourceFacts?> FindSourceAsync(MediaRenditionSource source, CancellationToken cancellationToken);

    /// <summary>The purposes that already have a row for this source, whatever the row says.</summary>
    Task<IReadOnlyList<MediaRenditionPurpose>> FindRecordedPurposesAsync(
        MediaRenditionSource source, CancellationToken cancellationToken);

    /// <summary>The row that holds one purpose's slot for this source, or null when nothing does yet.</summary>
    Task<MediaRendition?> FindRecordedAsync(
        MediaRenditionSource source, MediaRenditionPurpose purpose, CancellationToken cancellationToken);

    /// <summary>
    /// Renditions whose source no longer wants them, oldest first: a staged image that was declined,
    /// expired or kept, or a library asset that was deleted.
    /// </summary>
    /// <remarks>
    /// The work queue of the rendition purge. It empties, because purging a rendition removes its row.
    /// </remarks>
    Task<IReadOnlyList<MediaRendition>> FindPurgeableAsync(int limit, CancellationToken cancellationToken);

    void Add(MediaRendition rendition);

    void Remove(MediaRendition rendition);

    /// <summary>Stops tracking a row whose insert was refused, so the next save does not try it again.</summary>
    void Forget(MediaRendition rendition);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

internal sealed class MediaRenditionRepository(CreatorPantryDbContext context) : IMediaRenditionRepository
{
    public async Task<IReadOnlyList<MediaRendition>> FindForGeneratedImageAsync(
        Guid generatedImageId, CancellationToken cancellationToken) =>
        await context.MediaRenditions
            .AsNoTracking()
            .Where(rendition => rendition.GeneratedImageId == generatedImageId)
            .OrderBy(rendition => rendition.Purpose)
            .ToListAsync(cancellationToken);

    public Task<MediaRenditionSourceFacts?> FindSourceAsync(
        MediaRenditionSource source, CancellationToken cancellationToken) =>
        source.GeneratedImageId is { } imageId
            ? context.GeneratedImages
                .AsNoTracking()
                .Where(image => image.Id == imageId
                    && image.Status == GeneratedImageStatus.Staged
                    && image.ObjectDeletedAt == null)
                .Select(image => new MediaRenditionSourceFacts(image.ObjectKey, image.ContentChecksum, image.SizeBytes))
                .FirstOrDefaultAsync(cancellationToken)
            : context.MediaAssetVersions
                .AsNoTracking()
                .Where(version => version.MediaAssetId == source.MediaAssetId
                    && version.VersionNumber == source.MediaAssetVersionNumber
                    && context.MediaAssets.Any(asset => asset.Id == version.MediaAssetId && asset.DeletedAt == null))
                .Select(version => new MediaRenditionSourceFacts(version.ObjectKey, version.ContentChecksum, version.SizeBytes))
                .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<MediaRenditionPurpose>> FindRecordedPurposesAsync(
        MediaRenditionSource source, CancellationToken cancellationToken) =>
        await Of(source).Select(rendition => rendition.Purpose).ToListAsync(cancellationToken);

    public Task<MediaRendition?> FindRecordedAsync(
        MediaRenditionSource source, MediaRenditionPurpose purpose, CancellationToken cancellationToken) =>
        Of(source).FirstOrDefaultAsync(rendition => rendition.Purpose == purpose, cancellationToken);

    public async Task<IReadOnlyList<MediaRendition>> FindPurgeableAsync(
        int limit, CancellationToken cancellationToken) =>
        await context.MediaRenditions
            .Where(rendition =>

                // Anything but staged. A kept image's renditions were carried to the version made from it
                // in the transaction that kept it, so the staged ones are as redundant as its bytes.
                context.GeneratedImages.Any(image => image.Id == rendition.GeneratedImageId
                    && image.Status != GeneratedImageStatus.Staged)
                || context.MediaAssets.Any(asset => asset.Id == rendition.MediaAssetId
                    && asset.DeletedAt != null))
            .OrderBy(rendition => rendition.CreatedAt)
            .ThenBy(rendition => rendition.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public void Add(MediaRendition rendition) => context.MediaRenditions.Add(rendition);

    public void Remove(MediaRendition rendition) => context.MediaRenditions.Remove(rendition);

    public void Forget(MediaRendition rendition) => context.Entry(rendition).State = EntityState.Detached;

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        context.SaveChangesAsync(cancellationToken);

    private IQueryable<MediaRendition> Of(MediaRenditionSource source) =>
        source.GeneratedImageId is { } imageId
            ? context.MediaRenditions.AsNoTracking().Where(rendition => rendition.GeneratedImageId == imageId)
            : context.MediaRenditions.AsNoTracking().Where(rendition => rendition.MediaAssetId == source.MediaAssetId
                && rendition.MediaAssetVersionNumber == source.MediaAssetVersionNumber);
}
