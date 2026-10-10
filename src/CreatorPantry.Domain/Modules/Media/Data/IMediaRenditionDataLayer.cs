using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Gateways;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Media.Data;

/// <summary>
/// A picture renditions can be made for, and which of them it still lacks.
/// </summary>
/// <param name="ObjectKey">The source's own key. Read from, never written to.</param>
/// <param name="Missing">The purposes with no row yet, in purpose order.</param>
public sealed record MediaRenditionTarget(
    MediaRenditionSource Source,
    string ObjectKey,
    string ContentChecksum,
    long SizeBytes,
    IReadOnlyList<MediaRenditionPurpose> Missing);

/// <summary>
/// Storage and rows for renditions: making them, and removing the ones no picture wants any more.
/// </summary>
public interface IMediaRenditionDataLayer
{
    /// <summary>
    /// The picture and the purposes it has no row for, or null when it is not one renditions are made for.
    /// </summary>
    Task<MediaRenditionTarget?> FindTargetAsync(MediaRenditionSource source, CancellationToken cancellationToken);

    /// <summary>
    /// The source's bytes, whole. Throws <see cref="ObjectStoreUnavailableException"/> when they cannot be
    /// read right now, including when the object its row names is not there.
    /// </summary>
    Task<byte[]> ReadSourceAsync(MediaRenditionTarget target, CancellationToken cancellationToken);

    /// <summary>
    /// Stores one rendition beside its source and records it. Safe to repeat: a second call for the same
    /// source and purpose leaves one object and one row.
    /// </summary>
    Task StoreAsync(
        MediaRenditionTarget target,
        MediaRenditionPurpose purpose,
        byte[] content,
        int width,
        int height,
        CancellationToken cancellationToken);

    /// <summary>Records that a purpose has no rendition, and why. Safe to repeat.</summary>
    Task RecordNotCompressedAsync(
        MediaRenditionTarget target,
        MediaRenditionPurpose purpose,
        MediaRenditionReason reason,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records that a purpose will not be attempted again, and removes anything an attempt stored for it.
    /// </summary>
    /// <remarks>
    /// Throws <see cref="ObjectStoreUnavailableException"/>, having recorded nothing, when storage cannot be
    /// reached: an outcome that could not be checked is not one to write down.
    /// </remarks>
    Task RecordGaveUpAsync(
        MediaRenditionTarget target, MediaRenditionPurpose purpose, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the bytes and then the row of every rendition whose staged image was declined, expired or
    /// kept, or whose library asset was deleted. Returns how many rows went.
    /// </summary>
    Task<int> PurgeAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMediaRenditionDataLayer"/>
/// <remarks>
/// <para>
/// <strong>Making one: bytes first, then the row.</strong> The object is stored before a row names it, so
/// a row never describes bytes that are not there. If the process dies in between, the object is found by
/// the retry — the store is create-only and says so. The encoder is deterministic, so the retry's bytes
/// are the same bytes: it adopts the object it found and writes the row. One object and one row, however
/// many times it runs.
/// </para>
/// <para>
/// <strong>An object another delivery may still be recording is never deleted.</strong> The same request
/// can be delivered twice at once. Adopting rather than replacing means neither delivery removes bytes the
/// other is about to describe; and whichever row is written second finds the slot taken and, if what took
/// it says there is no rendition, removes its own object rather than leaving one nothing names.
/// </para>
/// <para>
/// <strong>Removing one: bytes first, then the row, and only the rendition's.</strong> The gateway cannot
/// name a source's object, so nothing here can remove an original. A row goes only once its object is known
/// to be gone; if storage cannot be reached the row stays where it is, still true, and is this same work on
/// the next pass.
/// </para>
/// <para>
/// <strong>The row is deleted rather than tombstoned,</strong> unlike a staged image's. Nothing refers to a
/// rendition, it records nothing a creator did, and it can be made again from its source — so there is no
/// history worth keeping, and a deleted row is what empties the queue.
/// </para>
/// </remarks>
internal sealed class MediaRenditionDataLayer(
    IMediaRenditionRepository renditions,
    IMediaRenditionObjectGateway objects,
    IGeneratedImageObjectGateway stagedObjects,
    IMediaAssetObjectGateway assetObjects,
    IWorkspaceContext workspace,
    IClock clock,
    ILogger<MediaRenditionDataLayer> logger) : IMediaRenditionDataLayer
{
    private const string RenditionMediaType = GeneratedImageInspector.JpegMediaType;

    private static readonly MediaRenditionPurpose[] Purposes = [MediaRenditionPurpose.Web, MediaRenditionPurpose.Thumbnail];

    public async Task<MediaRenditionTarget?> FindTargetAsync(
        MediaRenditionSource source, CancellationToken cancellationToken)
    {
        if (!source.IsValid || await renditions.FindSourceAsync(source, cancellationToken) is not { } facts)
        {
            return null;
        }

        var recorded = await renditions.FindRecordedPurposesAsync(source, cancellationToken);

        return new MediaRenditionTarget(
            source,
            facts.ObjectKey,
            facts.ContentChecksum,
            facts.SizeBytes,
            [.. Purposes.Where(purpose => !recorded.Contains(purpose))]);
    }

    public async Task<byte[]> ReadSourceAsync(MediaRenditionTarget target, CancellationToken cancellationToken)
    {
        // Through the source's own gateway, so the key is checked against this workspace exactly as any
        // other read of the picture is. The lease is what is disposed; the stream is the lease's.
        IAsyncDisposable? lease;
        Stream? content;

        if (target.Source.GeneratedImageId is not null)
        {
            var opened = await stagedObjects.OpenReadAsync(target.ObjectKey, cancellationToken);
            (lease, content) = (opened, opened?.Content);
        }
        else
        {
            var opened = await assetObjects.OpenReadAsync(target.ObjectKey, cancellationToken);
            (lease, content) = (opened, opened?.Content);
        }

        if (lease is null || content is null)
        {
            // A row that names bytes storage does not have. Not something a rendition can settle, and
            // possibly not permanent, so it is the caller's to retry and eventually to give up on.
            throw new ObjectStoreUnavailableException("The source picture's bytes could not be found.");
        }

        await using (lease)
        {
            // The caller has already refused a source larger than the limit, from its row. This holds the
            // read to the same limit in case the object is not the size its row says.
            return await ReadAllAsync(
                content, MediaPolicy.ImageMaxBytes, "The source picture is larger than its row says.", cancellationToken);
        }
    }

    /// <summary>Reads a stream whole, up to a limit, with any failure reported in this layer's own words.</summary>
    /// <remarks>
    /// A store wraps the failures of opening an object, but a stream that breaks part-way through is the
    /// transport's own exception, and its message is the provider's — a host, a request id. This is the
    /// boundary where that stops (external.md): what is thrown says only that the read failed.
    /// </remarks>
    private static async Task<byte[]> ReadAllAsync(
        Stream content, long limit, string overLimit, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[81920];

        try
        {
            int read;

            while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    throw new ObjectStoreUnavailableException(overLimit);
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or ObjectStoreUnavailableException))
        {
            throw new ObjectStoreUnavailableException("A stored object could not be read to its end.", exception);
        }

        return buffer.ToArray();
    }

    public async Task StoreAsync(
        MediaRenditionTarget target,
        MediaRenditionPurpose purpose,
        byte[] content,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(content);

        var write = await PutAsync(target, purpose, content, cancellationToken);
        var stored = write.Object;

        if (write.Outcome is MediaRenditionObjectWriteOutcome.AlreadyExists)
        {
            // Either another delivery of this request has finished, and its row owns that object, or an
            // attempt stored it and has not written the row — because it died, or because it is about to.
            // Asked of the database, not assumed.
            if (await IsRecordedAsync(target.Source, purpose, cancellationToken))
            {
                return;
            }

            stored = await AdoptAsync(target, purpose, content, cancellationToken);
        }

        if (stored is null)
        {
            // Unreachable storage, or a second attempt racing this one for the key. Either way nothing
            // can be recorded now and trying again is correct.
            throw new ObjectStoreUnavailableException($"The rendition could not be stored ({write.Outcome}).");
        }

        var rendition = NewRow(target, purpose);
        rendition.Status = MediaRenditionStatus.Ready;
        rendition.ObjectKey = stored.ObjectKey;
        rendition.MediaType = stored.MediaType;
        rendition.SizeBytes = stored.SizeBytes;
        rendition.ContentChecksum = stored.ContentChecksum;
        rendition.Width = width;
        rendition.Height = height;

        if (await RecordAsync(rendition, cancellationToken))
        {
            return;
        }

        // Another delivery recorded this purpose first. If its row is a rendition, it names this same key
        // and these same bytes, and the object stays. If it says there is none — it gave up as this one
        // was finishing — then nothing names the object just stored, and it goes.
        var holder = await renditions.FindRecordedAsync(target.Source, purpose, cancellationToken);

        if (!string.Equals(holder?.ObjectKey, stored.ObjectKey, StringComparison.Ordinal))
        {
            await objects.DeleteAsync(stored.ObjectKey, cancellationToken);
        }
    }

    /// <summary>
    /// The object already under a rendition's key, taken as this attempt's own when it is the same bytes;
    /// otherwise replaced. Null when it could not be settled either way.
    /// </summary>
    /// <remarks>
    /// The same bytes is the ordinary case: an earlier attempt, or one running beside this one, encoded the
    /// same picture with the same encoder. Those are left alone and described from what the store itself
    /// says of them. Anything else under the key is not a rendition this code would make — a different
    /// build's output, or damage — and only then is it removed.
    /// </remarks>
    private async Task<MediaRenditionObject?> AdoptAsync(
        MediaRenditionTarget target, MediaRenditionPurpose purpose, byte[] content, CancellationToken cancellationToken)
    {
        var key = MediaRenditionObjectKey.For(target.ObjectKey, purpose);

        await using (var existing = await objects.OpenReadAsync(key, cancellationToken))
        {
            if (existing is not null && existing.Object.SizeBytes == content.Length)
            {
                var bytes = await ReadAllAsync(
                    existing.Content, content.Length, "A stored rendition is larger than the store says.", cancellationToken);

                if (bytes.AsSpan().SequenceEqual(content))
                {
                    return new MediaRenditionObject(
                        key, existing.Object.SizeBytes, existing.Object.ContentChecksum, existing.Object.MediaType);
                }
            }
        }

        await objects.DeleteAsync(key, cancellationToken);

        return (await PutAsync(target, purpose, content, cancellationToken)).Object;
    }

    public Task RecordNotCompressedAsync(
        MediaRenditionTarget target,
        MediaRenditionPurpose purpose,
        MediaRenditionReason reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        var rendition = NewRow(target, purpose);
        rendition.Status = MediaRenditionStatus.NotCompressed;
        rendition.NotCompressedReason = reason;

        return RecordAsync(rendition, cancellationToken);
    }

    public async Task RecordGaveUpAsync(
        MediaRenditionTarget target, MediaRenditionPurpose purpose, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        // A source key this module could not have written has no rendition key, and so nothing stored.
        var hasKey = MediaRenditionObjectKey.TryFor(target.ObjectKey, purpose, out var key);

        if (hasKey)
        {
            // Asked before anything is written, and only asked: if storage cannot be reached this throws
            // and no outcome is recorded. Nothing is deleted yet, because an object here may belong to
            // another delivery that is about to record it.
            await using var probe = await objects.OpenReadAsync(key.ObjectKey, cancellationToken);
        }

        var rendition = NewRow(target, purpose);
        rendition.Status = MediaRenditionStatus.NotCompressed;
        rendition.NotCompressedReason = MediaRenditionReason.RetriesExhausted;

        // Only the delivery whose row took the slot may remove the object. If another recorded first, its
        // row decides what the object is; if it records after, it finds this row and removes its own.
        if (await RecordAsync(rendition, cancellationToken) && hasKey)
        {
            await objects.DeleteAsync(key.ObjectKey, cancellationToken);
        }
    }

    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        var due = await renditions.FindPurgeableAsync(MediaPolicy.RetentionBatchSize, cancellationToken);
        var purged = 0;

        foreach (var rendition in due)
        {
            if (rendition.ObjectKey is { } objectKey && !await RemoveObjectAsync(rendition, objectKey, cancellationToken))
            {
                continue;
            }

            renditions.Remove(rendition);
            purged++;
        }

        if (purged > 0)
        {
            await renditions.SaveChangesAsync(cancellationToken);
        }

        return purged;
    }

    private async Task<MediaRenditionObjectWrite> PutAsync(
        MediaRenditionTarget target, MediaRenditionPurpose purpose, byte[] content, CancellationToken cancellationToken)
    {
        var write = await objects.PutAsync(
            target.ObjectKey,
            purpose,
            new MemoryStream(content, writable: false),
            RenditionMediaType,
            content.Length,
            cancellationToken);

        if (write.Outcome is MediaRenditionObjectWriteOutcome.NotOwned)
        {
            // A source row whose key this workspace could not have written. Corrupt, and not something a
            // retry will change — but it is not this layer's to decide that a picture is unreadable.
            throw new InvalidOperationException("The source picture's key is not one this workspace could have written.");
        }

        return write;
    }

    private MediaRendition NewRow(MediaRenditionTarget target, MediaRenditionPurpose purpose) => new()
    {
        Id = Guid.NewGuid(),

        // Stamped by WorkspaceOwnershipInterceptor from the resolved context; set here only so the
        // in-memory row is coherent before it is saved.
        WorkspaceId = workspace.WorkspaceId,
        GeneratedImageId = target.Source.GeneratedImageId,
        MediaAssetId = target.Source.MediaAssetId,
        MediaAssetVersionNumber = target.Source.MediaAssetVersionNumber,
        Purpose = purpose,
        SourceContentChecksum = target.ContentChecksum,
        CreatedAt = clock.UtcNow,
    };

    /// <summary>
    /// Inserts one row, and says whether it was this row that took the slot. A refusal because the purpose
    /// already has a row is the same request delivered twice: not an error, and false. Any other refusal is
    /// thrown.
    /// </summary>
    private async Task<bool> RecordAsync(MediaRendition rendition, CancellationToken cancellationToken)
    {
        renditions.Add(rendition);

        try
        {
            await renditions.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateException)
        {
            // Before anything else: a failed save leaves the row Added, and the next purpose's row is the
            // very next write this scope makes.
            renditions.Forget(rendition);

            var source = new MediaRenditionSource(
                rendition.GeneratedImageId, rendition.MediaAssetId, rendition.MediaAssetVersionNumber);

            if (!await IsRecordedAsync(source, rendition.Purpose, CancellationToken.None))
            {
                throw;
            }

            return false;
        }
    }

    private async Task<bool> IsRecordedAsync(
        MediaRenditionSource source, MediaRenditionPurpose purpose, CancellationToken cancellationToken) =>
        (await renditions.FindRecordedPurposesAsync(source, cancellationToken)).Contains(purpose);

    /// <summary>False when the bytes may still be there, which is the one case the row has to outlive.</summary>
    private async Task<bool> RemoveObjectAsync(
        MediaRendition rendition, string objectKey, CancellationToken cancellationToken)
    {
        if (!objects.Owns(objectKey))
        {
            // Unreachable through any write this module makes, so a corrupt row rather than a routine miss.
            // Settled all the same, as the staged-image purge settles its own: the bytes cannot be reached
            // from here whatever happens, and keeping the row would mean retrying it forever. By id, never
            // by the key itself.
            logger.LogError(
                "Rendition {MediaRenditionId} has an object key this workspace could not have written; "
                    + "its bytes cannot be purged from here.",
                rendition.Id);

            return true;
        }

        try
        {
            // Whether or not an object was actually there: a delete that found nothing means the bytes are
            // gone, which is all the row's removal needs to be true.
            await objects.DeleteAsync(objectKey, cancellationToken);

            return true;
        }
        catch (ObjectStoreUnavailableException exception)
        {
            logger.LogWarning(
                exception, "Storage could not be reached to purge rendition {MediaRenditionId}.", rendition.Id);

            return false;
        }
    }
}
