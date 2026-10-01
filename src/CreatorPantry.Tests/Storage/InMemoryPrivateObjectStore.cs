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
    private readonly ConcurrentDictionary<(string Container, string Key), (StoredObject Object, byte[] Bytes)> _objects = new();

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

        return _objects.TryAdd((container, key), (stored, buffer.ToArray()))
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
