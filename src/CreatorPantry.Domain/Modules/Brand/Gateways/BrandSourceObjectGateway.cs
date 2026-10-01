using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Brand.Gateways;

/// <summary>
/// Puts, gets, copies and deletes the private objects behind brand source documents: the original uploads
/// and the text extracted from them.
/// </summary>
/// <remarks>
/// A caller names a document and version, never a key: keys are generated here, under the resolved
/// workspace's prefix. A key handed back in — one read from a row — is honoured only if it is one this
/// gateway would have written for the current workspace. The write and copy operations report an unreachable
/// store as <see cref="BrandSourceObjectWriteOutcome.Unavailable"/>, because their callers compensate; read
/// and delete let <see cref="ObjectStoreUnavailableException"/> through.
/// </remarks>
public interface IBrandSourceObjectGateway
{
    /// <summary>Stores an upload as the original of one document version. Create-only.</summary>
    Task<BrandSourceObjectWrite> PutOriginalAsync(
        Guid documentId, Guid versionId, Stream content, string mediaType, long maxBytes, CancellationToken cancellationToken);

    /// <summary>Stores one extraction's text for a document version. Create-only.</summary>
    Task<BrandSourceObjectWrite> PutExtractedTextAsync(
        Guid documentId, Guid versionId, int ordinal, Stream content, long maxBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a stored object, or returns null. Null covers a missing object, a malformed key and another
    /// workspace's key alike, so the answer discloses nothing.
    /// </summary>
    Task<BrandSourceObjectContent?> OpenReadAsync(string objectKey, CancellationToken cancellationToken);

    /// <summary>Copies an original in this workspace to be the original of another document version. Create-only.</summary>
    Task<BrandSourceObjectWrite> CopyOriginalAsync(
        string sourceObjectKey, Guid documentId, Guid versionId, CancellationToken cancellationToken);

    /// <summary>Removes a stored object. Idempotent, and a no-op for a key that is not this workspace's.</summary>
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IBrandSourceObjectGateway"/> over the shared <see cref="IPrivateObjectStore"/>. A gateway by
/// backend.md's definition: it owns the key grammar and the workspace check, and no product decision —
/// which formats are allowed, how large, who may upload — is made here.
/// </summary>
/// <remarks>
/// Nothing here parses or inspects content, issues a URL, or logs a key or a body. The workspace comes from
/// <see cref="IWorkspaceContext"/>, never from an argument.
/// </remarks>
public sealed class BrandSourceObjectGateway(
    IPrivateObjectStore store,
    IWorkspaceContext workspace,
    ILogger<BrandSourceObjectGateway> logger) : IBrandSourceObjectGateway
{
    private const string ExtractedTextMediaType = "text/plain; charset=utf-8";

    public Task<BrandSourceObjectWrite> PutOriginalAsync(
        Guid documentId, Guid versionId, Stream content, string mediaType, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        return PutAsync(
            BrandSourceObjectKey.ForOriginal(workspace.WorkspaceId, documentId, versionId),
            documentId, versionId, content, mediaType, maxBytes, cancellationToken);
    }

    public Task<BrandSourceObjectWrite> PutExtractedTextAsync(
        Guid documentId, Guid versionId, int ordinal, Stream content, long maxBytes, CancellationToken cancellationToken) =>
        PutAsync(
            BrandSourceObjectKey.ForExtractedText(workspace.WorkspaceId, documentId, versionId, ordinal),
            documentId, versionId, content, ExtractedTextMediaType, maxBytes, cancellationToken);

    public async Task<BrandSourceObjectContent?> OpenReadAsync(string objectKey, CancellationToken cancellationToken)
    {
        if (!IsOwnKey(objectKey, out _))
        {
            return null;
        }

        var stored = await store.OpenReadAsync(BrandSourceObjectKey.Container, objectKey, cancellationToken);

        return stored is null ? null : new BrandSourceObjectContent(Map(stored.Object), stored, stored.Content);
    }

    public async Task<BrandSourceObjectWrite> CopyOriginalAsync(
        string sourceObjectKey, Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        var destinationKey = BrandSourceObjectKey.ForOriginal(workspace.WorkspaceId, documentId, versionId);

        if (!IsOwnKey(sourceObjectKey, out var source) || source.ExtractionOrdinal is not null)
        {
            return new BrandSourceObjectWrite(BrandSourceObjectWriteOutcome.SourceNotFound);
        }

        try
        {
            await using var stored = await store.OpenReadAsync(BrandSourceObjectKey.Container, sourceObjectKey, cancellationToken);

            if (stored is null)
            {
                return new BrandSourceObjectWrite(BrandSourceObjectWriteOutcome.SourceNotFound);
            }

            // Streamed through this process rather than copied provider-side: it works the same under every
            // credential the store may hold, and the copy is measured and checksummed like any other write.
            return await PutAsync(
                destinationKey, documentId, versionId, stored.Content, stored.Object.MediaType, Math.Max(1, stored.Object.SizeBytes), cancellationToken);
        }
        catch (ObjectStoreUnavailableException exception)
        {
            return Unavailable(exception, documentId, versionId);
        }
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        if (!IsOwnKey(objectKey, out var parts))
        {
            return;
        }

        var existed = await store.DeleteAsync(BrandSourceObjectKey.Container, objectKey, cancellationToken);

        logger.LogInformation(
            "Brand source object delete. workspaceId={WorkspaceId} documentId={DocumentId} versionId={VersionId} existed={Existed}",
            parts.WorkspaceId, parts.DocumentId, parts.VersionId, existed);
    }

    private async Task<BrandSourceObjectWrite> PutAsync(
        string key, Guid documentId, Guid versionId, Stream content, string mediaType, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);

        ObjectWriteResult result;

        try
        {
            result = await store.PutAsync(BrandSourceObjectKey.Container, key, content, mediaType, maxBytes, cancellationToken);
        }
        catch (ObjectStoreUnavailableException exception)
        {
            return Unavailable(exception, documentId, versionId);
        }

        logger.LogInformation(
            "Brand source object write. workspaceId={WorkspaceId} documentId={DocumentId} versionId={VersionId} outcome={Outcome} sizeBytes={SizeBytes}",
            workspace.WorkspaceId, documentId, versionId, result.Outcome, result.Object?.SizeBytes);

        return result.Outcome switch
        {
            ObjectWriteOutcome.Stored => new BrandSourceObjectWrite(BrandSourceObjectWriteOutcome.Stored, Map(result.Object!)),
            ObjectWriteOutcome.AlreadyExists => new BrandSourceObjectWrite(BrandSourceObjectWriteOutcome.AlreadyExists),
            ObjectWriteOutcome.TooLarge => new BrandSourceObjectWrite(BrandSourceObjectWriteOutcome.TooLarge),
            _ => throw new InvalidOperationException($"Unhandled object write outcome '{result.Outcome}'."),
        };
    }

    /// <summary>A key this gateway wrote, for the workspace this request resolved. Finding a row is not authorization.</summary>
    private bool IsOwnKey(string? objectKey, out BrandSourceObjectKeyParts parts) =>
        BrandSourceObjectKey.TryParse(objectKey, out parts) && parts.WorkspaceId == workspace.WorkspaceId;

    private BrandSourceObjectWrite Unavailable(ObjectStoreUnavailableException exception, Guid documentId, Guid versionId)
    {
        logger.LogWarning(
            exception,
            "Brand source object store unavailable. workspaceId={WorkspaceId} documentId={DocumentId} versionId={VersionId}",
            workspace.WorkspaceId, documentId, versionId);

        return new BrandSourceObjectWrite(BrandSourceObjectWriteOutcome.Unavailable);
    }

    private static BrandSourceObject Map(StoredObject stored) =>
        new(stored.Key, stored.SizeBytes, stored.ContentChecksum, stored.MediaType);
}
