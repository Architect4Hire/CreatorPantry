using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Media.Data;

/// <summary>
/// Finds stored pictures that have no renditions and nothing on the way to make them (B-28, AF.5.5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A queue claim, so it reads past the workspace filter</strong> — the carve-out tenancy.md allows
/// for exactly this, because the backfill looks for work before it knows whose it is. It keeps both of that
/// carve-out's conditions: what comes back is <strong>identifiers only</strong> — a workspace id and the
/// picture's own ids, never a key, a checksum, a title or a byte — and nothing here acts on them. The
/// backfill turns each into the same request a new picture gets, and the handler of that request resolves
/// and validates the workspace before it reads anything else. This file is listed in
/// <c>BulkOperationBoundaryTests.Exemptions</c>.
/// </para>
/// <para>
/// <strong>The same eligibility the job itself uses</strong> (<see cref="IMediaRenditionRepository.FindSourceAsync"/>):
/// a staged image whose bytes are still there, or a version of an asset that has not been deleted. A
/// picture this found and the job then refused would be found again on every pass.
/// </para>
/// </remarks>
internal sealed class MediaRenditionClaimRepository(CreatorPantryDbContext context)
{
    /// <summary>One row per purpose is what a finished picture has.</summary>
    private const int PurposeCount = 2;

    /// <summary>
    /// Up to <paramref name="limit"/> pictures stored at or before <paramref name="storedBefore"/> that are
    /// missing a rendition row, oldest first, staged images before library versions.
    /// </summary>
    public async Task<IReadOnlyList<MediaRenditionRequestedEvent>> FindUnrenderedAsync(
        DateTimeOffset storedBefore, int limit, CancellationToken cancellationToken)
    {
        var images = await context.GeneratedImages
            .IgnoreQueryFilters()
            .Where(image => image.Status == GeneratedImageStatus.Staged
                && image.ObjectDeletedAt == null
                && image.CreatedAt <= storedBefore
                && context.MediaRenditions.IgnoreQueryFilters()
                    .Count(rendition => rendition.GeneratedImageId == image.Id) < PurposeCount)
            .OrderBy(image => image.CreatedAt)
            .ThenBy(image => image.Id)
            .Take(limit)
            .Select(image => new { image.WorkspaceId, image.Id })
            .ToListAsync(cancellationToken);

        var found = images
            .Select(image => MediaRenditionRequestedEvent.For(
                image.WorkspaceId, MediaRenditionSource.ForGeneratedImage(image.Id)))
            .ToList();

        if (found.Count >= limit)
        {
            return found;
        }

        var versions = await context.MediaAssetVersions
            .IgnoreQueryFilters()
            .Where(version => version.CreatedAt <= storedBefore
                && context.MediaAssets.IgnoreQueryFilters()
                    .Any(asset => asset.Id == version.MediaAssetId && asset.DeletedAt == null)
                && context.MediaRenditions.IgnoreQueryFilters()
                    .Count(rendition => rendition.MediaAssetId == version.MediaAssetId
                        && rendition.MediaAssetVersionNumber == version.VersionNumber) < PurposeCount)
            .OrderBy(version => version.CreatedAt)
            .ThenBy(version => version.Id)
            .Take(limit - found.Count)
            .Select(version => new { version.WorkspaceId, version.MediaAssetId, version.VersionNumber })
            .ToListAsync(cancellationToken);

        found.AddRange(versions.Select(version => MediaRenditionRequestedEvent.For(
            version.WorkspaceId, MediaRenditionSource.ForAssetVersion(version.MediaAssetId, version.VersionNumber))));

        return found;
    }

    /// <summary>Commits the requests the backfill staged in the outbox.</summary>
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        context.SaveChangesAsync(cancellationToken);
}
