using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Media.Data;

/// <summary>Persistence for stored picture analyses (AF.3.4).</summary>
/// <remarks>
/// No <c>WorkspaceId</c> predicate and no <c>IgnoreQueryFilters</c> anywhere here: every read happens inside
/// the filtered set, so another workspace's reading of a picture is not found rather than found and refused
/// (tenancy.md).
/// </remarks>
public interface IMediaPictureAnalysisRepository
{
    /// <summary>The stored reading of one version of a library asset, tracked, or null.</summary>
    Task<MediaPictureAnalysis?> FindForAssetAsync(
        Guid mediaAssetId, int versionNumber, CancellationToken cancellationToken);

    /// <summary>The stored reading of a generated image, tracked, or null.</summary>
    Task<MediaPictureAnalysis?> FindForGeneratedImageAsync(
        Guid generatedImageId, CancellationToken cancellationToken);

    void Add(MediaPictureAnalysis analysis);

    /// <summary>
    /// Lets go of a row whose save failed, so a later save in the same unit of work does not try it again.
    /// </summary>
    void Forget(MediaPictureAnalysis analysis);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMediaPictureAnalysisRepository"/>
internal sealed class MediaPictureAnalysisRepository(CreatorPantryDbContext context)
    : IMediaPictureAnalysisRepository
{
    public Task<MediaPictureAnalysis?> FindForAssetAsync(
        Guid mediaAssetId, int versionNumber, CancellationToken cancellationToken) =>
        context.MediaPictureAnalyses.FirstOrDefaultAsync(
            analysis => analysis.MediaAssetId == mediaAssetId
                && analysis.MediaAssetVersionNumber == versionNumber,
            cancellationToken);

    public Task<MediaPictureAnalysis?> FindForGeneratedImageAsync(
        Guid generatedImageId, CancellationToken cancellationToken) =>
        context.MediaPictureAnalyses.FirstOrDefaultAsync(
            analysis => analysis.GeneratedImageId == generatedImageId, cancellationToken);

    public void Add(MediaPictureAnalysis analysis) => context.MediaPictureAnalyses.Add(analysis);

    public void Forget(MediaPictureAnalysis analysis) => context.Entry(analysis).State = EntityState.Detached;

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        context.SaveChangesAsync(cancellationToken);
}
