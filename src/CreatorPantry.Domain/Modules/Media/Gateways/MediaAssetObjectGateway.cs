using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Media.Gateways;

/// <summary>One stored DAM object: the identity and measurements a <c>MediaAssetVersion</c> records.</summary>
public sealed record MediaAssetObject(
    string ObjectKey, long SizeBytes, string ContentChecksum, string MediaType);

public enum MediaAssetObjectWriteOutcome
{
    Stored = 1,

    /// <summary>An object already exists at that key. It is untouched.</summary>
    AlreadyExists = 2,

    /// <summary>The content ran past the limit. Nothing was stored.</summary>
    TooLarge = 3,

    /// <summary>Storage could not be reached. Nothing can be assumed stored.</summary>
    Unavailable = 4,
}

/// <param name="Object">Present exactly when the outcome is <see cref="MediaAssetObjectWriteOutcome.Stored"/>.</param>
public sealed record MediaAssetObjectWrite(
    MediaAssetObjectWriteOutcome Outcome, MediaAssetObject? Object = null);

/// <summary>
/// Puts and removes the private objects behind DAM asset versions.
/// </summary>
/// <remarks>
/// A caller names an asset and a version number, never a key: keys are generated here under the resolved
/// workspace's prefix, and a key handed back in is honoured only if it is one this gateway would have
/// written for the current workspace. The same shape as <c>IGeneratedImageObjectGateway</c>.
/// </remarks>
public interface IMediaAssetObjectGateway
{
    /// <summary>Stores one version's bytes. Create-only: an existing object is never overwritten.</summary>
    Task<MediaAssetObjectWrite> PutVersionAsync(
        Guid mediaAssetId,
        int versionNumber,
        Stream content,
        string mediaType,
        long maxBytes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes a stored object. Idempotent, and a no-op for a key that is not this workspace's.
    /// </summary>
    /// <remarks>
    /// The compensating half of a version write: when the rows that would have owned an object cannot be
    /// committed, the object has to go, or it is bytes nothing will ever read and nothing will ever delete.
    /// </remarks>
    Task<bool> DeleteAsync(string objectKey, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMediaAssetObjectGateway"/>
/// <remarks>
/// A gateway by backend.md's definition: it owns the key grammar, the workspace check and the byte bound,
/// and makes no product decision. Nothing here inspects content, issues a URL, or logs a key or a body. The
/// workspace comes from <see cref="IWorkspaceContext"/>, never from an argument (tenancy.md).
/// </remarks>
public sealed class MediaAssetObjectGateway(
    IPrivateObjectStore store,
    IWorkspaceContext workspace,
    ILogger<MediaAssetObjectGateway> logger) : IMediaAssetObjectGateway
{
    public async Task<MediaAssetObjectWrite> PutVersionAsync(
        Guid mediaAssetId,
        int versionNumber,
        Stream content,
        string mediaType,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);

        var key = MediaAssetObjectKey.For(workspace.WorkspaceId, mediaAssetId, versionNumber);
        ObjectWriteResult result;

        try
        {
            result = await store.PutAsync(
                MediaAssetObjectKey.Container, key, content, mediaType, maxBytes, cancellationToken);
        }
        catch (ObjectStoreUnavailableException exception)
        {
            logger.LogWarning(
                exception,
                "DAM storage unavailable. workspaceId={WorkspaceId} mediaAssetId={MediaAssetId} version={VersionNumber}",
                workspace.WorkspaceId, mediaAssetId, versionNumber);

            return new MediaAssetObjectWrite(MediaAssetObjectWriteOutcome.Unavailable);
        }

        logger.LogInformation(
            "DAM object write. workspaceId={WorkspaceId} mediaAssetId={MediaAssetId} version={VersionNumber} "
                + "outcome={Outcome} sizeBytes={SizeBytes}",
            workspace.WorkspaceId, mediaAssetId, versionNumber, result.Outcome, result.Object?.SizeBytes);

        return result.Outcome switch
        {
            ObjectWriteOutcome.Stored => new MediaAssetObjectWrite(
                MediaAssetObjectWriteOutcome.Stored,
                new MediaAssetObject(
                    result.Object!.Key, result.Object.SizeBytes, result.Object.ContentChecksum, result.Object.MediaType)),
            ObjectWriteOutcome.AlreadyExists => new MediaAssetObjectWrite(MediaAssetObjectWriteOutcome.AlreadyExists),
            ObjectWriteOutcome.TooLarge => new MediaAssetObjectWrite(MediaAssetObjectWriteOutcome.TooLarge),
            _ => throw new InvalidOperationException($"Unhandled object write outcome '{result.Outcome}'."),
        };
    }

    public async Task<bool> DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        // Finding a key is not authorization: a key this gateway would not have written for this workspace
        // is not this workspace's object, and a delete of it is refused rather than attempted.
        if (!MediaAssetObjectKey.TryParse(objectKey, out var parts)
            || parts.WorkspaceId != workspace.WorkspaceId)
        {
            return false;
        }

        var existed = await store.DeleteAsync(MediaAssetObjectKey.Container, objectKey, cancellationToken);

        logger.LogInformation(
            "DAM object delete. workspaceId={WorkspaceId} mediaAssetId={MediaAssetId} version={VersionNumber} existed={Existed}",
            parts.WorkspaceId, parts.MediaAssetId, parts.VersionNumber, existed);

        return existed;
    }
}
