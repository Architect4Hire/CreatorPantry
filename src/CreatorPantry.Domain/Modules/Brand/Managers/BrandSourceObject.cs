namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// One stored brand source object: its stable identity and what the store measured. The values a
/// <c>BrandSourceDocumentVersion</c> or <c>BrandSourceExtraction</c> row records.
/// </summary>
/// <param name="ObjectKey">A private pointer. Recorded in SQL; never returned to a client.</param>
/// <param name="ContentChecksum"><c>sha256:</c> and the digest of the stored bytes.</param>
public sealed record BrandSourceObject(string ObjectKey, long SizeBytes, string ContentChecksum, string MediaType);

public enum BrandSourceObjectWriteOutcome
{
    Stored = 1,

    /// <summary>An object already exists for that document version. It is untouched.</summary>
    AlreadyExists = 2,

    /// <summary>The content ran past the limit. Nothing was stored.</summary>
    TooLarge = 3,

    /// <summary>A copy's source is missing, malformed, or not this workspace's — deliberately one answer.</summary>
    SourceNotFound = 4,

    /// <summary>Storage could not be reached. Nothing can be assumed stored.</summary>
    Unavailable = 5,
}

/// <param name="Object">Present exactly when <paramref name="Outcome"/> is <see cref="BrandSourceObjectWriteOutcome.Stored"/>.</param>
public sealed record BrandSourceObjectWrite(BrandSourceObjectWriteOutcome Outcome, BrandSourceObject? Object = null);

/// <summary>A brand source object opened for reading. Disposing it releases the stream.</summary>
public sealed class BrandSourceObjectContent(BrandSourceObject sourceObject, IAsyncDisposable lease, Stream content) : IAsyncDisposable
{
    public BrandSourceObject Object { get; } = sourceObject;

    public Stream Content { get; } = content;

    public ValueTask DisposeAsync() => lease.DisposeAsync();
}
