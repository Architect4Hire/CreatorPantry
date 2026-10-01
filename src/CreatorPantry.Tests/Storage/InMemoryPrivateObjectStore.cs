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

    /// <summary>When set, every call fails the way an unreachable provider does.</summary>
    public bool Unavailable { get; set; }

    public IReadOnlyCollection<string> Keys => [.. _objects.Keys.Select(entry => entry.Key)];

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

        return Task.FromResult(_objects.TryGetValue((container, key), out var entry)
            ? new StoredObjectContent(entry.Object, new MemoryStream(entry.Bytes, writable: false))
            : null);
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
}
