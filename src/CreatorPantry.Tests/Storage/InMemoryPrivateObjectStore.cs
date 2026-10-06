using System.Collections.Concurrent;
using CreatorPantry.Domain.Managers.Storage;

namespace CreatorPantry.Tests.Storage;

/// <summary>
/// <see cref="IPrivateObjectStore"/> held in memory, for tests of what sits above the store. It meters content
/// through the same <see cref="MeteredReadStream"/> the Azure adapter uses, so size, checksum and the byte
/// limit behave identically; <c>AzureBlobPrivateObjectStoreTests</c> is what holds the real adapter to the
/// same contract.
/// </summary>
internal sealed class InMemoryPrivateObjectStore : IPrivateObjectStore
{
    private readonly ConcurrentDictionary<(string Container, string Key), (StoredObject Object, byte[] Bytes, DateTimeOffset CreatedAt)> _objects = new();

    /// <summary>When a write made now is recorded as happening. Movable, so a grace period can be tested.</summary>
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    private int _openReads;

    /// <summary>When set, every call fails the way an unreachable provider does.</summary>
    public bool Unavailable { get; set; }

    public IReadOnlyCollection<string> Keys => [.. _objects.Keys.Select(entry => entry.Key)];

    /// <summary>
    /// How many reads this store has handed out and not had back.
    /// </summary>
    /// <remarks>
    /// A real provider holds a connection open for a streaming read, so a caller that drops a
    /// <see cref="StoredObjectContent"/> without disposing it leaks one per request until the pool is
    /// exhausted. A <c>MemoryStream</c> cannot fail that way, which means a test over this fake would pass
    /// whether or not the code released anything — so the fake counts instead, and the download tests assert
    /// this is back to zero on every path including the ones that send no body.
    /// </remarks>
    public int OpenReads => Volatile.Read(ref _openReads);

    public async Task<ObjectWriteResult> PutAsync(
        string container, string key, Stream content, string mediaType, long maxBytes, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();

        await using var metered = new MeteredReadStream(content, maxBytes);
        using var buffer = new MemoryStream();

        try
        {
            await metered.CopyToAsync(buffer, cancellationToken);
        }
        catch (ObjectTooLargeException)
        {
            return new ObjectWriteResult(ObjectWriteOutcome.TooLarge);
        }

        var stored = new StoredObject(key, metered.BytesRead, metered.Checksum, mediaType);

        return _objects.TryAdd((container, key), (stored, buffer.ToArray(), Now))
            ? new ObjectWriteResult(ObjectWriteOutcome.Stored, stored)
            : new ObjectWriteResult(ObjectWriteOutcome.AlreadyExists);
    }

    public Task<StoredObjectContent?> OpenReadAsync(string container, string key, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();

        if (!_objects.TryGetValue((container, key), out var entry))
        {
            // Nothing was handed out, so nothing is owed back.
            return Task.FromResult<StoredObjectContent?>(null);
        }

        Interlocked.Increment(ref _openReads);

        return Task.FromResult<StoredObjectContent?>(new StoredObjectContent(
            entry.Object, new CountedStream(new MemoryStream(entry.Bytes, writable: false), this)));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <strong>Key order and real continuation tokens, because that is what the Azure adapter does.</strong>
    /// An earlier version of this fake sorted by age before taking a page, which made orphan reconciliation
    /// look correct in every test while the real store silently never listed past its first page — a key is
    /// a pair of GUIDs, so key order is nothing like age order. A fake that is kinder than the real thing
    /// is worse than no fake.
    /// </para>
    /// <para>
    /// The write time is the clock at <c>PutAsync</c>, which a test can move, so a sweep's grace period can
    /// be exercised without waiting for one.
    /// </para>
    /// </remarks>
    public Task<ObjectListPage> ListAsync(
        string container, string prefix, int pageSize, string? continuationToken, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var matching = _objects
            .Where(entry => entry.Key.Container == container
                && entry.Key.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(entry => new StoredObjectSummary(
                entry.Key.Key, entry.Value.Object.SizeBytes, entry.Value.CreatedAt))
            .OrderBy(summary => summary.Key, StringComparer.Ordinal)
            .ToList();

        // The token is the last key handed out, so a page resumes strictly after it. A real store's token
        // is opaque; this one only has to behave like one.
        var start = continuationToken is null
            ? 0
            : matching.FindIndex(summary => string.CompareOrdinal(summary.Key, continuationToken) > 0);

        if (start < 0)
        {
            return Task.FromResult(new ObjectListPage([]));
        }

        var page = matching.Skip(start).Take(pageSize).ToList();
        var more = start + page.Count < matching.Count;

        return Task.FromResult(new ObjectListPage(page, more ? page[^1].Key : null));
    }

    public Task<bool> DeleteAsync(string container, string key, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();

        return Task.FromResult(_objects.TryRemove((container, key), out _));
    }

    private void ThrowIfUnavailable()
    {
        if (Unavailable)
        {
            throw new ObjectStoreUnavailableException("The test store is switched off.");
        }
    }

    /// <summary>
    /// The bytes of one read, which tells the store when it has them back.
    /// </summary>
    /// <remarks>
    /// Counts once however it is closed and however many times: a caller that disposes twice has still
    /// returned one read, and a double dispose must not read as a leak in the other direction. Both
    /// <see cref="DisposeAsync"/> and <see cref="Dispose"/> are overridden because a stream may be released
    /// either way and the response pipeline chooses.
    /// </remarks>
    private sealed class CountedStream(Stream inner, InMemoryPrivateObjectStore store) : Stream
    {
        private int _returned;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            Return();
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Return();
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Return()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0)
            {
                Interlocked.Decrement(ref store._openReads);
            }
        }
    }
}
