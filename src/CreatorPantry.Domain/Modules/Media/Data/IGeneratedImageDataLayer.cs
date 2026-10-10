using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Media.Gateways;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Media.Data;

/// <summary>One staged image opened for reading, with the facts a response needs beside the bytes.</summary>
public sealed class StagedImageDownload : IAsyncDisposable
{
    private readonly IAsyncDisposable lease;

    public StagedImageDownload(GeneratedImageObjectContent content, int variantIndex, string fileName)
    {
        ArgumentNullException.ThrowIfNull(content);

        lease = content;
        Content = content.Content;
        MediaType = content.Object.MediaType;
        SizeBytes = content.Object.SizeBytes;
        ContentChecksum = content.Object.ContentChecksum;
        VariantIndex = variantIndex;
        FileName = fileName;
    }

    /// <summary>A rendition of the image rather than the image as it was staged (AF.5.6).</summary>
    public StagedImageDownload(
        StoredObjectContent content, int variantIndex, string fileName, MediaRenditionPurpose rendition)
    {
        ArgumentNullException.ThrowIfNull(content);

        lease = content;
        Content = content.Content;
        MediaType = content.Object.MediaType;
        SizeBytes = content.Object.SizeBytes;
        ContentChecksum = content.Object.ContentChecksum;
        VariantIndex = variantIndex;
        FileName = fileName;
        Rendition = rendition;
    }

    public Stream Content { get; }

    public string MediaType { get; }

    public long SizeBytes { get; }

    public string ContentChecksum { get; }

    public int VariantIndex { get; }

    public string FileName { get; }

    /// <summary>
    /// Which rendition these bytes are, or null when they are the image as it was staged.
    /// </summary>
    /// <remarks>
    /// What was served, not what was asked for: a caller that asked for a rendition the image does not
    /// have gets the original, and this is null.
    /// </remarks>
    public MediaRenditionPurpose? Rendition { get; }

    public ValueTask DisposeAsync() => lease.DisposeAsync();
}

/// <summary>Why a staged image could not be opened.</summary>
public enum StagedImageOpenOutcome
{
    Opened = 1,

    /// <summary>
    /// No such image in the resolved workspace, or its bytes are gone.
    /// </summary>
    /// <remarks>
    /// One outcome for several causes on purpose: an unknown id, a neighbour's id, and an image whose
    /// bytes the sweep has already removed are all "not here" to a caller, and distinguishing them would
    /// let a client count another workspace's images or learn when one expired.
    /// </remarks>
    NotFound = 2,

    /// <summary>
    /// The row is there and its bytes could not be reached.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="NotFound"/>: the image exists and retrying is the remedy, which is a
    /// different thing to tell a creator than "it is gone". The same split the brand source download makes.
    /// </remarks>
    StorageUnavailable = 3,
}

/// <param name="Download">Present exactly when the outcome is <see cref="StagedImageOpenOutcome.Opened"/>.</param>
public sealed record StagedImageOpen(StagedImageOpenOutcome Outcome, StagedImageDownload? Download = null);

/// <summary>What rejecting a staged image did.</summary>
public enum StagedImageRejectOutcome
{
    /// <summary>It was staged and is now rejected. Its bytes are the sweep's to remove.</summary>
    Rejected = 1,

    /// <summary>It was already rejected. Nothing changed, and that is a success.</summary>
    AlreadyRejected = 2,

    /// <summary>No such image in the resolved workspace.</summary>
    NotFound = 3,

    /// <summary>
    /// It is kept or expired, and neither can be rejected.
    /// </summary>
    /// <remarks>
    /// Every state but <c>Staged</c> is terminal (12.6). A kept image belongs to whatever was made from it
    /// and a creator who changes their mind deletes that; an expired one has no bytes left to decline.
    /// </remarks>
    NotRejectable = 4,
}

/// <summary>A staged image opened so a DAM asset can be made from it, with the facts a version needs.</summary>
/// <param name="Width">From the staged row, established when the worker read the provider's bytes (12.7).</param>
public sealed record StagedImageForKeep(
    StagedImageOpenOutcome Outcome, StagedImageDownload? Download = null, int Width = 0, int Height = 0);

/// <summary>What one retention pass did for one workspace.</summary>
/// <param name="Renditions">Rendition rows removed with their bytes, staged or library (AF.5.4).</param>
public sealed record StagedImageRetentionSummary(int Expired, int Purged, int Orphans, int Renditions = 0);

/// <summary>Composes the staged-image library's persistence operations (IMG-005, IMG-006).</summary>
public interface IGeneratedImageDataLayer
{
    /// <inheritdoc cref="IGeneratedImageRepository.ExistsAsync"/>
    Task<bool> ExistsAsync(Guid generatedImageId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IGeneratedImageRepository.IsAvailableAsync"/>
    Task<bool> IsAvailableAsync(Guid generatedImageId, CancellationToken cancellationToken);

    /// <summary>Opens one staged image of the resolved workspace for reading.</summary>
    Task<StagedImageOpen> OpenAsync(Guid generatedImageId, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a staged image as <see cref="OpenAsync(Guid, CancellationToken)"/> does, serving the named
    /// rendition in place of the original when the image has one.
    /// </summary>
    /// <remarks>
    /// The same eligibility, decided first and by the same code. A rendition that does not exist is not an
    /// error: the original is served and the result says so.
    /// </remarks>
    Task<StagedImageOpen> OpenAsync(
        Guid generatedImageId, MediaRenditionPurpose? rendition, CancellationToken cancellationToken);

    /// <summary>
    /// What one available generated image holds, without touching storage: its type, size and checksum
    /// (AF.3.4).
    /// </summary>
    /// <returns>
    /// Null for an unknown image, another workspace's, and one that is declined, expired or purged — the same
    /// images <see cref="OpenAsync"/> answers as not found, so asking first and opening later cannot disagree.
    /// </returns>
    Task<MediaPictureTarget?> ResolvePictureAsync(Guid generatedImageId, CancellationToken cancellationToken);

    /// <summary>Opens an available generated image's bytes for another module to read (AF.3.4).</summary>
    /// <remarks>
    /// <see cref="OpenAsync"/> itself, with only its answer reshaped: there is one rule for which images may
    /// be read, and a second copy of it here would be a softer way in the day the two drifted.
    /// </remarks>
    Task<MediaPictureOpen> OpenPictureAsync(Guid generatedImageId, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a staged image so DAM-001 can copy it, and reports the dimensions its row recorded.
    /// </summary>
    /// <remarks>
    /// <see cref="StagedImageOpenOutcome.NotFound"/> covers an unknown id, a neighbour's, one already
    /// kept, rejected or expired, and one whose bytes retention has removed. Only a <c>Staged</c> image
    /// can become an asset, and the caller has no business learning which of those it was.
    /// </remarks>
    Task<StagedImageForKeep> OpenForKeepAsync(Guid generatedImageId, CancellationToken cancellationToken);

    /// <summary>Marks one staged image rejected. Idempotent.</summary>
    /// <remarks>
    /// The row moves and the bytes do not. A request that also deleted the object would be a database
    /// write and a storage call with no transaction over them, so a failure between the two would leave a
    /// row saying "rejected" beside bytes nothing would ever collect, or bytes gone from under a row that
    /// still says "staged". The sweep owns deletion, and the row is the truth it works from.
    /// </remarks>
    Task<StagedImageRejectOutcome> RejectAsync(Guid generatedImageId, CancellationToken cancellationToken);

    /// <summary>Moves staged images of this workspace past their deadline to expired.</summary>
    Task<int> ExpireAsync(CancellationToken cancellationToken);

    /// <summary>Removes the bytes of this workspace's rejected and expired images, and records that.</summary>
    Task<int> PurgeAsync(CancellationToken cancellationToken);

    /// <summary>Removes this workspace's staging objects that no row owns and that are old enough to judge.</summary>
    Task<int> ReconcileOrphansAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGeneratedImageDataLayer"/>
/// <remarks>
/// <para>
/// <strong>The row moves before the bytes do, always.</strong> Expiry commits the status change and only
/// then deletes; purge deletes and only then records that it did. A crash between the two leaves work for
/// the next pass rather than an inconsistency: an expired row whose bytes are still there is simply
/// purgeable, and an object whose row never recorded the delete is deleted again — which a store reports
/// as "there was nothing to remove" rather than failing.
/// </para>
/// <para>
/// Nothing here decides policy. How long an image lives, how old an orphan must be, how many rows a pass
/// takes: all of it is <see cref="MediaPolicy"/>.
/// </para>
/// </remarks>
internal sealed class GeneratedImageDataLayer(
    IGeneratedImageRepository images,
    IGeneratedImageObjectGateway objects,
    IMediaRenditionRepository renditions,
    IMediaRenditionObjectGateway renditionObjects,
    IClock clock,
    ILogger<GeneratedImageDataLayer> logger) : IGeneratedImageDataLayer
{
    public Task<bool> ExistsAsync(Guid generatedImageId, CancellationToken cancellationToken) =>
        images.ExistsAsync(generatedImageId, cancellationToken);

    public Task<bool> IsAvailableAsync(Guid generatedImageId, CancellationToken cancellationToken) =>
        images.IsAvailableAsync(generatedImageId, cancellationToken);

    public Task<StagedImageOpen> OpenAsync(Guid generatedImageId, CancellationToken cancellationToken) =>
        OpenAsync(generatedImageId, rendition: null, cancellationToken);

    public async Task<StagedImageOpen> OpenAsync(
        Guid generatedImageId, MediaRenditionPurpose? rendition, CancellationToken cancellationToken)
    {
        var image = await images.FindAsync(generatedImageId, cancellationToken);

        // ObjectDeletedAt is checked as well as the row: a purged image still has a row, and opening it
        // would be a storage round trip that can only ever come back empty.
        //
        // So is the status (12.10l). A picture the creator declined, and one nobody chose in time, are no
        // longer theirs to look at: the bytes linger only until the sweep collects them, and serving them in
        // the meantime would make "declined" mean "declined, eventually". Answered as not found, exactly as
        // it will be once the sweep has run, so the answer does not change under the caller. A kept image is
        // still served: its asset holds the bytes for good and this copy is the same picture.
        if (image is null
            || image.ObjectDeletedAt is not null
            || image.Status is GeneratedImageStatus.Rejected or GeneratedImageStatus.Expired)
        {
            return new StagedImageOpen(StagedImageOpenOutcome.NotFound);
        }

        // Only now, with the picture itself established as one this caller may be shown: the smaller
        // encoding of it, when it was asked for and there is one (AF.5.6). Everything above decides who
        // sees the picture; this only decides how many bytes it takes.
        if (rendition is { } purpose
            && await MediaRenditionOpener.TryOpenAsync(
                renditions, renditionObjects, MediaRenditionSource.ForGeneratedImage(image.Id), purpose, cancellationToken)
                is { } smaller)
        {
            return new StagedImageOpen(
                StagedImageOpenOutcome.Opened,
                new StagedImageDownload(
                    smaller,
                    image.VariantIndex,
                    GeneratedImageDownloadFileName.For(image.VariantIndex, smaller.Object.MediaType),
                    purpose));
        }

        GeneratedImageObjectContent? content;

        try
        {
            content = await objects.OpenReadAsync(image.ObjectKey, cancellationToken);
        }
        catch (ObjectStoreUnavailableException exception)
        {
            logger.LogWarning(
                exception, "Staging storage could not be reached to read image {GeneratedImageId}.", generatedImageId);

            return new StagedImageOpen(StagedImageOpenOutcome.StorageUnavailable);
        }

        if (content is null)
        {
            // A row naming bytes that are not there. Unavailable rather than not-found: the image exists as
            // far as the creator is concerned, and a 404 would invite them to stop looking for it.
            logger.LogError(
                "Image {GeneratedImageId} has a row but no object in staging storage.", generatedImageId);

            return new StagedImageOpen(StagedImageOpenOutcome.StorageUnavailable);
        }

        return new StagedImageOpen(
            StagedImageOpenOutcome.Opened,
            new StagedImageDownload(
                content,
                image.VariantIndex,

                // The stored media type, established from the bytes at staging — so the extension
                // describes what is there rather than repeating anything a provider claimed (IMG-005).
                GeneratedImageDownloadFileName.For(image.VariantIndex, image.MediaType)));
    }

    public async Task<MediaPictureTarget?> ResolvePictureAsync(
        Guid generatedImageId, CancellationToken cancellationToken)
    {
        var image = await images.FindAsync(generatedImageId, cancellationToken);

        // The rule OpenAsync applies, applied to the row alone.
        return image is null
            || image.ObjectDeletedAt is not null
            || image.Status is GeneratedImageStatus.Rejected or GeneratedImageStatus.Expired
                ? null
                : new MediaPictureTarget(null, image.MediaType, image.SizeBytes, image.ContentChecksum);
    }

    public async Task<MediaPictureOpen> OpenPictureAsync(
        Guid generatedImageId, CancellationToken cancellationToken)
    {
        var opened = await OpenAsync(generatedImageId, cancellationToken);

        return opened.Outcome switch
        {
            StagedImageOpenOutcome.Opened => new MediaPictureOpen(
                MediaPictureOpenOutcome.Opened,
                new MediaPictureContent(
                    opened.Download!,
                    opened.Download!.Content,
                    opened.Download.MediaType,
                    opened.Download.ContentChecksum,
                    versionNumber: null)),
            StagedImageOpenOutcome.StorageUnavailable => new MediaPictureOpen(MediaPictureOpenOutcome.StorageUnavailable),
            _ => new MediaPictureOpen(MediaPictureOpenOutcome.NotFound),
        };
    }

    public async Task<StagedImageForKeep> OpenForKeepAsync(
        Guid generatedImageId, CancellationToken cancellationToken)
    {
        var image = await images.FindAsync(generatedImageId, cancellationToken);

        // Only a staged image is keepable. Every other state is terminal (12.6), and a kept one already
        // has an asset — which the caller checked for before reaching here.
        if (image is null || image.Status is not GeneratedImageStatus.Staged || image.ObjectDeletedAt is not null)
        {
            return new StagedImageForKeep(StagedImageOpenOutcome.NotFound);
        }

        var opened = await OpenAsync(generatedImageId, cancellationToken);

        return opened.Outcome is StagedImageOpenOutcome.Opened
            ? new StagedImageForKeep(opened.Outcome, opened.Download, image.Width, image.Height)
            : new StagedImageForKeep(opened.Outcome);
    }

    public async Task<StagedImageRejectOutcome> RejectAsync(
        Guid generatedImageId, CancellationToken cancellationToken)
    {
        var image = await images.FindAsync(generatedImageId, cancellationToken);

        if (image is null)
        {
            return StagedImageRejectOutcome.NotFound;
        }

        if (image.Status is GeneratedImageStatus.Rejected)
        {
            // A repeat. Saying so rather than refusing: the creator asked for this image to be gone and it
            // is, which is the answer they wanted however many times they ask.
            return StagedImageRejectOutcome.AlreadyRejected;
        }

        if (image.Status is not GeneratedImageStatus.Staged)
        {
            return StagedImageRejectOutcome.NotRejectable;
        }

        image.Status = GeneratedImageStatus.Rejected;
        image.StatusChangedAt = clock.UtcNow;

        await images.SaveChangesAsync(cancellationToken);

        return StagedImageRejectOutcome.Rejected;
    }

    public async Task<int> ExpireAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var due = await images.FindExpiredAsync(now, MediaPolicy.RetentionBatchSize, cancellationToken);

        if (due.Count == 0)
        {
            return 0;
        }

        foreach (var image in due)
        {
            image.Status = GeneratedImageStatus.Expired;
            image.StatusChangedAt = now;
        }

        // Status first and bytes later, by the next pass's purge. An expiry that deleted as it went would
        // have to decide what to do when the delete failed after the row had moved.
        await images.SaveChangesAsync(cancellationToken);

        return due.Count;
    }

    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        var due = await images.FindPurgeableAsync(MediaPolicy.RetentionBatchSize, cancellationToken);
        var purged = 0;

        foreach (var image in due)
        {
            if (!objects.Owns(image.ObjectKey))
            {
                // Unreachable through any write this module makes — keys are generated from the resolved
                // workspace, the operation and the variant — so this is a corrupt row rather than a
                // routine miss. Logged at error because nothing else will ever notice it, and still
                // settled: the bytes cannot be reached from here whatever happens, and leaving the row in
                // the purge queue would mean retrying it forever, which is the thing ObjectDeletedAt
                // exists to prevent. By image id, never by the key itself.
                logger.LogError(
                    "Image {GeneratedImageId} has an object key this workspace could not have written; "
                        + "its bytes cannot be purged from here.",
                    image.Id);

                image.ObjectDeletedAt = clock.UtcNow;
                purged++;

                continue;
            }

            try
            {
                await objects.DeleteAsync(image.ObjectKey, cancellationToken);
            }
            catch (ObjectStoreUnavailableException exception)
            {
                // Left for the next pass. The row still says the bytes are there, which is true, so
                // nothing downstream is misled — and the index this read from is exactly the work queue.
                logger.LogWarning(
                    exception, "Staging storage could not be reached to purge image {GeneratedImageId}.", image.Id);

                continue;
            }

            // Whether or not an object was actually there. A delete that found nothing means the bytes are
            // gone, which is what this column records — and leaving it null would keep an already-empty
            // key in the purge queue for good.
            image.ObjectDeletedAt = clock.UtcNow;
            purged++;
        }

        if (purged > 0)
        {
            await images.SaveChangesAsync(cancellationToken);
        }

        return purged;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// The one sweep that works from storage rather than from rows, because an orphan is by definition
    /// invisible to every query over the database: its row was never committed.
    /// </para>
    /// <para>
    /// <strong>Two conditions before anything is deleted.</strong> No row of this workspace names the key —
    /// which is what "never deletes an object something owns" means in practice, and it is asked of the
    /// database rather than assumed — and the object is older than
    /// <see cref="MediaPolicy.OrphanGracePeriod"/>, because a generation writes its object before it
    /// commits the row and an in-flight image is indistinguishable from an orphan until that window closes.
    /// </para>
    /// <para>
    /// <strong>It walks the prefix rather than sampling it.</strong> A blob store lists in key order and a
    /// staging key is a pair of GUIDs, so key order is effectively random — taking one page and sorting it
    /// by age would mean an orphan whose key sorts late was never looked at, not merely looked at later.
    /// Each page costs one ownership query rather than one per object, and
    /// <see cref="MediaPolicy.ReconciliationMaxPages"/> bounds the walk so one enormous workspace cannot
    /// hold the sweep open; what it does not reach this hour it reaches the next.
    /// </para>
    /// <para>
    /// The listing is this workspace's prefix only, so there is no neighbour's object in the set to begin
    /// with. A key that does not parse was dropped by the gateway and never reaches here.
    /// </para>
    /// </remarks>
    public async Task<int> ReconcileOrphansAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow - MediaPolicy.OrphanGracePeriod;
        var removed = 0;
        string? continuation = null;
        var pages = 0;

        do
        {
            GeneratedImageObjectPage page;

            try
            {
                page = await objects.ListAsync(
                    MediaPolicy.ReconciliationPageSize, continuation, cancellationToken);
            }
            catch (ObjectStoreUnavailableException exception)
            {
                logger.LogWarning(exception, "Staging storage could not be listed for orphan reconciliation.");

                break;
            }

            continuation = page.ContinuationToken;
            pages++;

            // Only the ones old enough to judge are worth asking the database about.
            var candidates = page.Objects.Where(candidate => candidate.CreatedAt <= cutoff).ToList();

            if (candidates.Count == 0)
            {
                continue;
            }

            var owned = await images.FindOwnedObjectKeysAsync(
                [.. candidates.Select(candidate => candidate.ObjectKey)], cancellationToken);

            foreach (var orphan in candidates.Where(candidate => !owned.Contains(candidate.ObjectKey)))
            {
                try
                {
                    if (await objects.DeleteAsync(orphan.ObjectKey, cancellationToken))
                    {
                        removed++;
                    }
                }
                catch (ObjectStoreUnavailableException exception)
                {
                    logger.LogWarning(
                        exception, "Staging storage could not be reached to remove an orphaned object.");

                    continuation = null;

                    break;
                }
            }
        }
        while (continuation is not null && pages < MediaPolicy.ReconciliationMaxPages
            && !cancellationToken.IsCancellationRequested);

        if (removed > 0)
        {
            // By count and never by key: a key names a workspace and an operation, and an operations log
            // is not a place for either (tenancy.md).
            logger.LogInformation("Removed {Removed} orphaned staging objects.", removed);
        }

        return removed;
    }
}
