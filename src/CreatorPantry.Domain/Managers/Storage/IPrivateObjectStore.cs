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

    /// <summary>
    /// One page of the objects under <paramref name="prefix"/>, in the store's own order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For reconciling storage against the rows that are supposed to own it. An object whose row was never
    /// committed is invisible to every query over the database — only the container knows it is there — so
    /// a sweep that can find one has to be able to enumerate.
    /// </para>
    /// <para>
    /// <strong>Paged, and in the store's order rather than any order this chooses.</strong> A blob store
    /// lists in key order, and a key is not a timestamp — so a method that took the first page and sorted
    /// it by age would silently never return anything past that page. Honest paging is the difference
    /// between a sweep that is slow and one that is wrong: a caller follows
    /// <see cref="ObjectListPage.ContinuationToken"/> until it is null, and sees everything.
    /// </para>
    /// <para>
    /// <paramref name="prefix"/> is matched literally and is the caller's only filter, so an empty one
    /// would enumerate the whole container across every workspace in it. Implementations refuse one, which
    /// is a guard rather than a policy: scoping is the gateway's job, and this makes the careless case
    /// throw rather than succeed quietly.
    /// so a caller that wants one workspace's objects passes that workspace's prefix. It returns what the
    /// store knows without opening anything: no bytes are read and no content is returned.
    /// </para>
    /// </remarks>
    /// <param name="continuationToken">Null for the first page; otherwise the previous page's token.</param>
    Task<ObjectListPage> ListAsync(
        string container,
        string prefix,
        int pageSize,
        string? continuationToken,
        CancellationToken cancellationToken);

    /// <summary>Removes the object. Idempotent: false when there was nothing to remove.</summary>
    Task<bool> DeleteAsync(string container, string key, CancellationToken cancellationToken);
}

/// <summary>What is known about one stored object. <paramref name="ContentChecksum"/> is <c>sha256:</c> and the hex digest.</summary>
public sealed record StoredObject(string Key, long SizeBytes, string ContentChecksum, string MediaType);

/// <summary>
/// One object as a listing reports it: its key and when the store says it was written.
/// </summary>
/// <remarks>
/// Deliberately not a <see cref="StoredObject"/>. A listing does not read an object, so it cannot state a
/// checksum, and a type carrying a checksum field this could not fill would invite a caller to trust an
/// empty one. <paramref name="CreatedAt"/> is what lets a reconciliation leave recent objects alone — an
/// object written seconds ago may be one a worker is still about to commit a row for.
/// </remarks>
public sealed record StoredObjectSummary(string Key, long SizeBytes, DateTimeOffset CreatedAt);

/// <summary>One page of a listing. <paramref name="ContinuationToken"/> is null on the last page.</summary>
public sealed record ObjectListPage(
    IReadOnlyList<StoredObjectSummary> Objects, string? ContinuationToken = null);

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
