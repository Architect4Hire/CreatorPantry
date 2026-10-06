using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Media.Gateways;

/// <summary>One stored staging object: the identity and measurements a <c>GeneratedImage</c> row records.</summary>
/// <param name="ObjectKey">A private pointer. Recorded in SQL; never returned to a client.</param>
/// <param name="ContentChecksum"><c>sha256:</c> and the digest of the stored bytes.</param>
public sealed record GeneratedImageObject(
    string ObjectKey, long SizeBytes, string ContentChecksum, string MediaType);

public enum GeneratedImageObjectWriteOutcome
{
    Stored = 1,

    /// <summary>An object already exists at that key. It is untouched.</summary>
    AlreadyExists = 2,

    /// <summary>The content ran past <see cref="MediaPolicy.ImageMaxBytes"/>. Nothing was stored.</summary>
    TooLarge = 3,

    /// <summary>Storage could not be reached. Nothing can be assumed stored.</summary>
    Unavailable = 4,
}

/// <param name="Object">Present exactly when the outcome is <see cref="GeneratedImageObjectWriteOutcome.Stored"/>.</param>
public sealed record GeneratedImageObjectWrite(
    GeneratedImageObjectWriteOutcome Outcome, GeneratedImageObject? Object = null);

/// <summary>
/// Puts and deletes the private objects behind staged generated images.
/// </summary>
/// <remarks>
/// A caller names an operation and a variant, never a key: keys are generated here under the resolved
/// workspace's prefix, and a key handed back in — one read from a row — is honoured only if it is one this
/// gateway would have written for the current workspace. The same shape as
/// <c>IBrandSourceObjectGateway</c>, which is the pattern for private creator objects in this codebase.
/// </remarks>
public interface IGeneratedImageObjectGateway
{
    /// <summary>Stores one variant's bytes. Create-only: an existing object is never overwritten.</summary>
    Task<GeneratedImageObjectWrite> PutVariantAsync(
        Guid operationId, int variantIndex, ReadOnlyMemory<byte> content, string mediaType, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a stored object, or returns null.
    /// </summary>
    /// <remarks>
    /// Null covers a missing object, a malformed key and another workspace's key alike, so the answer
    /// discloses nothing about which of the three it was (tenancy.md). The caller disposes what it gets.
    /// </remarks>
    Task<GeneratedImageObjectContent?> OpenReadAsync(string objectKey, CancellationToken cancellationToken);

    /// <summary>
    /// One page of the resolved workspace's staging objects, for reconciling storage against rows.
    /// </summary>
    /// <remarks>
    /// Scoped to this workspace's prefix, so a reconciliation can never see — let alone remove — a
    /// neighbour's object. Keys that do not parse as ones this gateway would have written are dropped
    /// here rather than handed up: whatever they are, they are not this workspace's staged images, and a
    /// sweep has no business deciding what to do with them. Follow the continuation token until it is
    /// null; a page is in the store's key order, which is not age order.
    /// </remarks>
    Task<GeneratedImageObjectPage> ListAsync(
        int pageSize, string? continuationToken, CancellationToken cancellationToken);

    /// <summary>Whether a key is one this gateway would have written for the resolved workspace.</summary>
    /// <remarks>
    /// So a caller can tell a delete that found nothing from one that was refused. Both answer false, and
    /// they mean opposite things: the first is the work being done and the second is a row pointing
    /// somewhere it has no business pointing.
    /// </remarks>
    bool Owns(string? objectKey);

    /// <summary>
    /// Removes a stored object. Idempotent, and a no-op for a key that is not this workspace's.
    /// </summary>
    /// <remarks>
    /// This is the compensating half of a staging write: when the row that would have referenced an object
    /// cannot be committed, the object has to go, or it becomes an orphan nothing will ever read and
    /// nothing will ever delete. Returns whether an object was actually removed so the caller can tell a
    /// successful compensation from one that found nothing.
    /// </remarks>
    Task<bool> DeleteAsync(string objectKey, CancellationToken cancellationToken);

    /// <summary>
    /// Removes one variant's object by naming the variant rather than the key.
    /// </summary>
    /// <remarks>
    /// For clearing an orphan left by an attempt that died between writing the object and committing the
    /// row that owns it. The caller has no key to pass, because the row that would have held one was never
    /// written — but the key is deterministic, so the variant names it.
    /// </remarks>
    Task<bool> DeleteVariantAsync(Guid operationId, int variantIndex, CancellationToken cancellationToken);
}

/// <summary>One staging object as a listing reports it: where it is, and when it was written.</summary>
public sealed record GeneratedImageObjectSummary(
    string ObjectKey, Guid OperationId, int VariantIndex, long SizeBytes, DateTimeOffset CreatedAt);

/// <summary>One page of staging objects. <paramref name="ContinuationToken"/> is null on the last page.</summary>
public sealed record GeneratedImageObjectPage(
    IReadOnlyList<GeneratedImageObjectSummary> Objects, string? ContinuationToken = null);

/// <summary>A staged image opened for reading. Disposing it releases the stream.</summary>
public sealed class GeneratedImageObjectContent(
    GeneratedImageObject storedObject, IAsyncDisposable lease, Stream content) : IAsyncDisposable
{
    public GeneratedImageObject Object { get; } = storedObject;

    public Stream Content { get; } = content;

    public ValueTask DisposeAsync() => lease.DisposeAsync();
}


/// <summary>
/// <see cref="IGeneratedImageObjectGateway"/> over the shared <see cref="IPrivateObjectStore"/>.
/// </summary>
/// <remarks>
/// A gateway by backend.md's definition: it owns the key grammar, the workspace check and the byte bound,
/// and makes no product decision — which formats are allowed, how many variants, who may generate — none of
/// which is decided here. Nothing here inspects content, issues a URL, or logs a key or a body. The
/// workspace comes from <see cref="IWorkspaceContext"/>, never from an argument (tenancy.md).
/// </remarks>
public sealed class GeneratedImageObjectGateway(
    IPrivateObjectStore store,
    IWorkspaceContext workspace,
    ILogger<GeneratedImageObjectGateway> logger) : IGeneratedImageObjectGateway
{
    public async Task<GeneratedImageObjectWrite> PutVariantAsync(
        Guid operationId, int variantIndex, ReadOnlyMemory<byte> content, string mediaType, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        var key = GeneratedImageObjectKey.For(workspace.WorkspaceId, operationId, variantIndex);
        ObjectWriteResult result;

        try
        {
            using var bytes = AsStream(content);

            result = await store.PutAsync(
                GeneratedImageObjectKey.Container, key, bytes, mediaType, MediaPolicy.ImageMaxBytes, cancellationToken);
        }
        catch (ObjectStoreUnavailableException exception)
        {
            logger.LogWarning(
                exception,
                "Staging storage unavailable. workspaceId={WorkspaceId} operationId={OperationId} variantIndex={VariantIndex}",
                workspace.WorkspaceId, operationId, variantIndex);

            return new GeneratedImageObjectWrite(GeneratedImageObjectWriteOutcome.Unavailable);
        }

        logger.LogInformation(
            "Staged image write. workspaceId={WorkspaceId} operationId={OperationId} variantIndex={VariantIndex} "
                + "outcome={Outcome} sizeBytes={SizeBytes}",
            workspace.WorkspaceId, operationId, variantIndex, result.Outcome, result.Object?.SizeBytes);

        return result.Outcome switch
        {
            ObjectWriteOutcome.Stored => new GeneratedImageObjectWrite(
                GeneratedImageObjectWriteOutcome.Stored,
                new GeneratedImageObject(
                    result.Object!.Key, result.Object.SizeBytes, result.Object.ContentChecksum, result.Object.MediaType)),
            ObjectWriteOutcome.AlreadyExists => new GeneratedImageObjectWrite(GeneratedImageObjectWriteOutcome.AlreadyExists),
            ObjectWriteOutcome.TooLarge => new GeneratedImageObjectWrite(GeneratedImageObjectWriteOutcome.TooLarge),
            _ => throw new InvalidOperationException($"Unhandled object write outcome '{result.Outcome}'."),
        };
    }

    public async Task<GeneratedImageObjectContent?> OpenReadAsync(
        string objectKey, CancellationToken cancellationToken)
    {
        if (!IsOwnKey(objectKey, out _))
        {
            return null;
        }

        var stored = await store.OpenReadAsync(
            GeneratedImageObjectKey.Container, objectKey, cancellationToken);

        return stored is null
            ? null
            : new GeneratedImageObjectContent(
                new GeneratedImageObject(
                    stored.Object.Key, stored.Object.SizeBytes, stored.Object.ContentChecksum, stored.Object.MediaType),
                stored,
                stored.Content);
    }

    public async Task<GeneratedImageObjectPage> ListAsync(
        int pageSize, string? continuationToken, CancellationToken cancellationToken)
    {
        var page = await store.ListAsync(
            GeneratedImageObjectKey.Container,
            GeneratedImageObjectKey.PrefixFor(workspace.WorkspaceId),
            pageSize,
            continuationToken,
            cancellationToken);

        return new GeneratedImageObjectPage(
            [
                .. page.Objects
                    .Select(summary => (summary, parsed: GeneratedImageObjectKey.TryParse(summary.Key, out var parts), parts))
                    .Where(entry => entry.parsed && entry.parts.WorkspaceId == workspace.WorkspaceId)
                    .Select(entry => new GeneratedImageObjectSummary(
                        entry.summary.Key,
                        entry.parts.OperationId,
                        entry.parts.VariantIndex,
                        entry.summary.SizeBytes,
                        entry.summary.CreatedAt)),
            ],
            page.ContinuationToken);
    }

    public bool Owns(string? objectKey) => IsOwnKey(objectKey, out _);

    public Task<bool> DeleteVariantAsync(Guid operationId, int variantIndex, CancellationToken cancellationToken) =>
        DeleteAsync(
            GeneratedImageObjectKey.For(workspace.WorkspaceId, operationId, variantIndex), cancellationToken);

    public async Task<bool> DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        // Finding a key is not authorization: a key this gateway would not have written for this workspace
        // is not this workspace's object, and a delete of it is refused rather than attempted.
        if (!IsOwnKey(objectKey, out var parts))
        {
            return false;
        }

        var existed = await store.DeleteAsync(GeneratedImageObjectKey.Container, objectKey, cancellationToken);

        logger.LogInformation(
            "Staged image delete. workspaceId={WorkspaceId} operationId={OperationId} variantIndex={VariantIndex} existed={Existed}",
            parts.WorkspaceId, parts.OperationId, parts.VariantIndex, existed);

        return existed;
    }
    /// <summary>A key this gateway wrote, for the workspace this request resolved. Finding a key is not authorization.</summary>
    private bool IsOwnKey(string? objectKey, out GeneratedImageObjectKeyParts parts) =>
        GeneratedImageObjectKey.TryParse(objectKey, out parts) && parts.WorkspaceId == workspace.WorkspaceId;

    /// <summary>
    /// A read-only stream over the provider's bytes, without copying them where the memory allows it.
    /// </summary>
    /// <remarks>
    /// An image can be tens of megabytes and the store meters whatever it is handed, so the copy
    /// <c>ToArray</c> would make is worth avoiding — but only when the memory is array-backed, which a
    /// <see cref="Microsoft.Extensions.AI.DataContent"/> built from a response body is and one built from a
    /// slice of a pooled buffer may not be.
    /// </remarks>
    private static Stream AsStream(ReadOnlyMemory<byte> content) =>
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(content, out var segment) && segment.Array is not null
            ? new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(content.ToArray(), writable: false);
}
