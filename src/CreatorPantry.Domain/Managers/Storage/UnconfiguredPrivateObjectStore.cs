namespace CreatorPantry.Domain.Managers.Storage;

/// <summary>
/// The store a host gets when no object storage is configured: it refuses everything. It exists so a host can
/// start, and its dependency graph validate, without storage — a test host, a clean clone — while anything
/// that actually reaches for an object fails loudly instead of appearing to work.
/// </summary>
internal sealed class UnconfiguredPrivateObjectStore : IPrivateObjectStore
{
    public Task<ObjectWriteResult> PutAsync(
        string container, string key, Stream content, string mediaType, long maxBytes, CancellationToken cancellationToken) =>
        throw NotConfigured();

    public Task<StoredObjectContent?> OpenReadAsync(string container, string key, CancellationToken cancellationToken) =>
        throw NotConfigured();

    public Task<ObjectListPage> ListAsync(
        string container, string prefix, int pageSize, string? continuationToken, CancellationToken cancellationToken) =>
        throw NotConfigured();

    public Task<bool> DeleteAsync(string container, string key, CancellationToken cancellationToken) =>
        throw NotConfigured();

    private static ObjectStoreUnavailableException NotConfigured() =>
        new($"No object storage is configured: the '{ObjectStorageConnection.Name}' connection is missing.");
}
