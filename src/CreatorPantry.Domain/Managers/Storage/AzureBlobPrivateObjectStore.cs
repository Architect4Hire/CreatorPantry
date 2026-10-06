using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace CreatorPantry.Domain.Managers.Storage;

/// <summary>
/// <see cref="IPrivateObjectStore"/> over Azure Blob Storage — Azurite in a local run (B-05). The only type
/// that names the blob SDK; every SDK type and exception is mapped here and none leaves.
/// </summary>
/// <remarks>
/// <para>
/// It does not create containers. A container and its private access level are infrastructure the AppHost
/// declares, so a missing one is a deployment fault this reports as unavailable rather than papering over.
/// </para>
/// <para>
/// Retries are the SDK's own, which repeat only what is safe to repeat. A write is conditional on the key
/// being free, so a repeated write cannot replace an object.
/// </para>
/// </remarks>
internal sealed class AzureBlobPrivateObjectStore(BlobServiceClient client) : IPrivateObjectStore
{
    private const string ChecksumMetadataKey = "sha256";

    private const string ChecksumPrefix = "sha256:";

    public async Task<ObjectWriteResult> PutAsync(
        string container, string key, Stream content, string mediaType, long maxBytes, CancellationToken cancellationToken)
    {
        var blob = client.GetBlobContainerClient(container).GetBlobClient(key);
        await using var metered = new MeteredReadStream(content, maxBytes);

        try
        {
            await blob.UploadAsync(
                metered,
                new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders { ContentType = mediaType },
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                },
                cancellationToken);
        }
        catch (Exception exception) when (IsTooLarge(exception))
        {
            return new ObjectWriteResult(ObjectWriteOutcome.TooLarge);
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            return new ObjectWriteResult(ObjectWriteOutcome.AlreadyExists);
        }
        catch (RequestFailedException exception)
        {
            throw Unavailable("write", exception);
        }

        // The digest is only known once the content has streamed, so it is recorded in a second call. An
        // object without it is not one this store can describe, so a failure here takes the object back out.
        try
        {
            await blob.SetMetadataAsync(
                new Dictionary<string, string> { [ChecksumMetadataKey] = metered.Checksum[ChecksumPrefix.Length..] },
                cancellationToken: cancellationToken);
        }
        catch (RequestFailedException exception)
        {
            await blob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: CancellationToken.None);
            throw Unavailable("write", exception);
        }

        return new ObjectWriteResult(
            ObjectWriteOutcome.Stored, new StoredObject(key, metered.BytesRead, metered.Checksum, mediaType));
    }

    public async Task<StoredObjectContent?> OpenReadAsync(string container, string key, CancellationToken cancellationToken)
    {
        var blob = client.GetBlobContainerClient(container).GetBlobClient(key);

        try
        {
            var download = (await blob.DownloadStreamingAsync(cancellationToken: cancellationToken)).Value;
            var details = download.Details;

            if (!details.Metadata.TryGetValue(ChecksumMetadataKey, out var digest))
            {
                await download.Content.DisposeAsync();
                throw new ObjectStoreUnavailableException("A stored object has no recorded checksum.");
            }

            return new StoredObjectContent(
                new StoredObject(key, details.ContentLength, ChecksumPrefix + digest, details.ContentType),
                download.Content);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
        catch (RequestFailedException exception)
        {
            throw Unavailable("read", exception);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One page in the SDK's own order, which is key order. Nothing is re-sorted here: a page sorted by
    /// age would be a lie about what the next page contains, and the caller needs to be able to walk the
    /// whole prefix.
    /// </remarks>
    public async Task<ObjectListPage> ListAsync(
        string container, string prefix, int pageSize, string? continuationToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        try
        {
            await foreach (var page in client.GetBlobContainerClient(container)
                .GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken)
                .AsPages(continuationToken, pageSize))
            {
                return new ObjectListPage(
                    [
                        .. page.Values.Select(blob => new StoredObjectSummary(
                            blob.Name,
                            blob.Properties.ContentLength ?? 0,

                            // A blob always has one; the fallback keeps a null out of a sweep's age
                            // arithmetic, where it would read as year one and make the object look ancient.
                            blob.Properties.CreatedOn ?? DateTimeOffset.MinValue)),
                    ],

                    // Empty rather than null is how the SDK spells "no more pages" on the last one.
                    string.IsNullOrEmpty(page.ContinuationToken) ? null : page.ContinuationToken);
            }
        }
        catch (RequestFailedException exception)
        {
            throw Unavailable("list", exception);
        }

        return new ObjectListPage([]);
    }

    public async Task<bool> DeleteAsync(string container, string key, CancellationToken cancellationToken)
    {
        var blob = client.GetBlobContainerClient(container).GetBlobClient(key);

        try
        {
            return (await blob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken)).Value;
        }
        catch (RequestFailedException exception)
        {
            throw Unavailable("delete", exception);
        }
    }

    /// <summary>The SDK may hand the stream's own exception back directly or wrapped in an aggregate.</summary>
    private static bool IsTooLarge(Exception exception) =>
        exception is ObjectTooLargeException
        || (exception is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Any(inner => inner is ObjectTooLargeException));

    // The provider's message can name the account and container; it stays on the inner exception.
    private static ObjectStoreUnavailableException Unavailable(string operation, RequestFailedException exception) =>
        new($"The object store refused a {operation} (status {exception.Status}).", exception);
}
