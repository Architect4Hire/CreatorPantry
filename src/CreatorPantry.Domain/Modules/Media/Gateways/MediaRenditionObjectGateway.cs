using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Media.Gateways;

public sealed record MediaRenditionObject(
    string ObjectKey, long SizeBytes, string ContentChecksum, string MediaType);

public enum MediaRenditionObjectWriteOutcome
{
    Stored = 1,

    /// <summary>An object already exists at that key. It is untouched.</summary>
    AlreadyExists = 2,

    /// <summary>The content ran past the limit. Nothing was stored.</summary>
    TooLarge = 3,

    /// <summary>Storage could not be reached. Nothing can be assumed stored.</summary>
    Unavailable = 4,

    /// <summary>The source key is not one this workspace could have written. Nothing was attempted.</summary>
    NotOwned = 5,
}

public sealed record MediaRenditionObjectWrite(
    MediaRenditionObjectWriteOutcome Outcome, MediaRenditionObject? Object = null);

/// <summary>
/// Private storage for rendition bytes, beside the picture each was made from.
/// </summary>
/// <remarks>
/// The same shape as <see cref="IMediaAssetObjectGateway"/> and <see cref="IGeneratedImageObjectGateway"/>,
/// and the same rule: a key is checked, not trusted. The workspace comes from
/// <see cref="IWorkspaceContext"/>, never from an argument (tenancy.md), and a key that names another
/// workspace is answered as absent. Nothing here can name a source's own object, so nothing here can
/// overwrite or remove an original.
/// </remarks>
public interface IMediaRenditionObjectGateway
{
    /// <summary>Stores one rendition beside its source. Create-only: an existing object is never overwritten.</summary>
    Task<MediaRenditionObjectWrite> PutAsync(
        string sourceObjectKey,
        MediaRenditionPurpose purpose,
        Stream content,
        string mediaType,
        long maxBytes,
        CancellationToken cancellationToken);

    /// <summary>Opens a rendition's bytes, or null when this workspace has no rendition under that key.</summary>
    Task<StoredObjectContent?> OpenReadAsync(string objectKey, CancellationToken cancellationToken);

    /// <summary>Whether <paramref name="objectKey"/> is a rendition key of the resolved workspace.</summary>
    bool Owns(string? objectKey);

    /// <summary>Removes a rendition's object. Idempotent, and a no-op for a key that is not this workspace's.</summary>
    Task<bool> DeleteAsync(string objectKey, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMediaRenditionObjectGateway"/>
public sealed class MediaRenditionObjectGateway(
    IPrivateObjectStore store,
    IWorkspaceContext workspace,
    ILogger<MediaRenditionObjectGateway> logger) : IMediaRenditionObjectGateway
{
    public async Task<MediaRenditionObjectWrite> PutAsync(
        string sourceObjectKey,
        MediaRenditionPurpose purpose,
        Stream content,
        string mediaType,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);

        // The one grammar decides both the container and the owner.
        if (!MediaRenditionObjectKey.TryFor(sourceObjectKey, purpose, out var parts)
            || parts.WorkspaceId != workspace.WorkspaceId)
        {
            return new MediaRenditionObjectWrite(MediaRenditionObjectWriteOutcome.NotOwned);
        }

        var key = parts.ObjectKey;
        ObjectWriteResult result;

        try
        {
            result = await store.PutAsync(parts.Container, key, content, mediaType, maxBytes, cancellationToken);
        }
        catch (ObjectStoreUnavailableException exception)
        {
            logger.LogWarning(
                exception,
                "Rendition storage unavailable. workspaceId={WorkspaceId} purpose={Purpose}",
                workspace.WorkspaceId, purpose);

            return new MediaRenditionObjectWrite(MediaRenditionObjectWriteOutcome.Unavailable);
        }

        logger.LogInformation(
            "Rendition object write. workspaceId={WorkspaceId} purpose={Purpose} outcome={Outcome} sizeBytes={SizeBytes}",
            workspace.WorkspaceId, purpose, result.Outcome, result.Object?.SizeBytes);

        return result.Outcome switch
        {
            ObjectWriteOutcome.Stored => new MediaRenditionObjectWrite(
                MediaRenditionObjectWriteOutcome.Stored,
                new MediaRenditionObject(
                    result.Object!.Key, result.Object.SizeBytes, result.Object.ContentChecksum, result.Object.MediaType)),
            ObjectWriteOutcome.AlreadyExists => new MediaRenditionObjectWrite(MediaRenditionObjectWriteOutcome.AlreadyExists),
            ObjectWriteOutcome.TooLarge => new MediaRenditionObjectWrite(MediaRenditionObjectWriteOutcome.TooLarge),
            _ => throw new InvalidOperationException($"Unhandled object write outcome '{result.Outcome}'."),
        };
    }

    public async Task<StoredObjectContent?> OpenReadAsync(string objectKey, CancellationToken cancellationToken) =>
        IsOwnKey(objectKey, out var parts)
            ? await store.OpenReadAsync(parts.Container, objectKey, cancellationToken)
            : null;

    public bool Owns(string? objectKey) => IsOwnKey(objectKey, out _);

    public async Task<bool> DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        // Finding a key is not authorization: a key this gateway would not have written for this workspace
        // is not this workspace's object, and a delete of it is refused rather than attempted. A source's
        // own key does not parse as a rendition's, so an original cannot be removed through here.
        if (!IsOwnKey(objectKey, out var parts))
        {
            return false;
        }

        var existed = await store.DeleteAsync(parts.Container, objectKey, cancellationToken);

        logger.LogInformation(
            "Rendition object delete. workspaceId={WorkspaceId} purpose={Purpose} existed={Existed}",
            parts.WorkspaceId, parts.Purpose, existed);

        return existed;
    }

    private bool IsOwnKey(string? objectKey, out MediaRenditionObjectKeyParts parts) =>
        MediaRenditionObjectKey.TryParse(objectKey, out parts) && parts.WorkspaceId == workspace.WorkspaceId;
}
