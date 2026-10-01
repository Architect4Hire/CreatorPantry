namespace CreatorPantry.Domain.Managers.Storage;

/// <summary>
/// Bytes in a private container, addressed by a key the caller generated. Provider-neutral: the one seam a
/// storage SDK sits behind, shared by every module that keeps objects.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Not a place for policy.</strong> It does not know what a workspace is, what a key means, or who
/// may read it. The typed gateway above it generates and validates keys; this stores what it is handed. That
/// is also why nothing but a module gateway should take a dependency on it.
/// </para>
/// <para>
/// <strong>No addresses.</strong> Nothing here returns a URL, a signed link or a provider type, so nothing
/// above it can leak one (media.md).
/// </para>
/// <para>
/// Expected outcomes are results. A provider that cannot be reached, or answers with something this contract
/// has no word for, is an <see cref="ObjectStoreUnavailableException"/>.
/// </para>
/// </remarks>
public interface IPrivateObjectStore
{
    /// <summary>
    /// Writes a new object, measuring it as it streams. Create-only: an existing object is never overwritten.
    /// Reading past <paramref name="maxBytes"/> abandons the write and leaves no object behind.
    /// </summary>
    Task<ObjectWriteResult> PutAsync(
        string container, string key, Stream content, string mediaType, long maxBytes, CancellationToken cancellationToken);

    /// <summary>The object and a stream over its bytes, or null when there is none. The caller disposes it.</summary>
    Task<StoredObjectContent?> OpenReadAsync(string container, string key, CancellationToken cancellationToken);

    /// <summary>Removes the object. Idempotent: false when there was nothing to remove.</summary>
    Task<bool> DeleteAsync(string container, string key, CancellationToken cancellationToken);
}

/// <summary>What is known about one stored object. <paramref name="ContentChecksum"/> is <c>sha256:</c> and the hex digest.</summary>
public sealed record StoredObject(string Key, long SizeBytes, string ContentChecksum, string MediaType);

public enum ObjectWriteOutcome
{
    Stored = 1,

    /// <summary>The key is taken. The existing object is untouched.</summary>
    AlreadyExists = 2,

    /// <summary>The content ran past the limit. Nothing was stored.</summary>
    TooLarge = 3,
}

/// <param name="Object">Present exactly when <paramref name="Outcome"/> is <see cref="ObjectWriteOutcome.Stored"/>.</param>
public sealed record ObjectWriteResult(ObjectWriteOutcome Outcome, StoredObject? Object = null);

/// <summary>A stored object opened for reading. Disposing it releases the stream.</summary>
public sealed class StoredObjectContent(StoredObject storedObject, Stream content) : IAsyncDisposable
{
    public StoredObject Object { get; } = storedObject;

    public Stream Content { get; } = content;

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>
/// The object store could not be used. Carries no provider type; the provider's own exception is the inner one.
/// </summary>
public sealed class ObjectStoreUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
