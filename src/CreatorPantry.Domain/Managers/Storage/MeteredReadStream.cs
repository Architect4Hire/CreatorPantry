using System.Security.Cryptography;

namespace CreatorPantry.Domain.Managers.Storage;

/// <summary>
/// Reads through to another stream while counting and hashing what passes, and refuses to pass more than a
/// limit. How a store learns an object's size and checksum without buffering it, and stops an oversized one
/// before it is committed.
/// </summary>
/// <remarks>
/// Deliberately not seekable, so a storage SDK streams through it once rather than rewinding and reading
/// bytes this has already counted. It does not own the inner stream.
/// </remarks>
internal sealed class MeteredReadStream(Stream inner, long maxBytes) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    private string? _checksum;

    public long BytesRead { get; private set; }

    /// <summary><c>sha256:</c> and the digest of everything read. Final: call it once the content is consumed.</summary>
    public string Checksum => _checksum ??= "sha256:" + Convert.ToHexStringLower(_hash.GetHashAndReset());

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        Meter(buffer[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        Meter(buffer.Span[..read]);
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Meter(ReadOnlySpan<byte> read)
    {
        BytesRead += read.Length;

        if (BytesRead > maxBytes)
        {
            throw new ObjectTooLargeException();
        }

        _hash.AppendData(read);
    }
}

/// <summary>The content ran past its limit. Caught by the store, which reports <see cref="ObjectWriteOutcome.TooLarge"/>.</summary>
internal sealed class ObjectTooLargeException : Exception;
