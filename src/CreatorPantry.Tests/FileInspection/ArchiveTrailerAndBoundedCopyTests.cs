using System.Buffers.Binary;
using CreatorPantry.Domain.Managers.FileInspection;
using CreatorPantry.Tests.Brand;

namespace CreatorPantry.Tests.FileInspection;

public sealed class ArchiveTrailerTests
{
    [Fact]
    public void A_zip_ends_in_its_own_directory()
    {
        Assert.True(ArchiveTrailer.EndsInZipDirectory(HostileSamples.Zip()));
    }

    [Fact]
    public void A_zip_after_anything_is_still_a_zip()
    {
        Assert.True(ArchiveTrailer.EndsInZipDirectory(HostileSamples.Concat(BrandSourceSampleFiles.Pdf(), HostileSamples.Zip())));
    }

    [Fact]
    public void A_comment_that_runs_over_the_end_is_followed_to_it()
    {
        Assert.True(ArchiveTrailer.EndsInZipDirectory(HostileSamples.PdfHidingZip()));
    }

    [Fact]
    public void The_signature_alone_is_not_a_directory()
    {
        // The four bytes, but with a comment length that does not land on the end: chance, not an archive.
        var bytes = new byte[200];
        "PK\u0005\u0006"u8.CopyTo(bytes.AsSpan(50));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(70), 3);

        Assert.False(ArchiveTrailer.EndsInZipDirectory(bytes));
    }

    [Fact]
    public void A_directory_past_the_furthest_a_reader_looks_is_not_found()
    {
        var zip = HostileSamples.Zip();
        var padded = HostileSamples.Concat(zip, new byte[ArchiveTrailer.ZipSearchWindow]);

        Assert.False(ArchiveTrailer.EndsInZipDirectory(padded));
        Assert.False(HostileSamples.OpensAsZip(padded));
    }

    [Fact]
    public void Nothing_and_a_whole_picture_are_not_archives()
    {
        Assert.False(ArchiveTrailer.EndsInZipDirectory([]));
        Assert.False(ArchiveTrailer.EndsInZipDirectory(BrandSourceSampleFiles.Png()));
    }
}

public sealed class BoundedCopyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Exactly_the_cap_is_copied_whole()
    {
        using var destination = new MemoryStream();

        Assert.True(await BoundedCopy.TryCopyAsync(new MemoryStream(new byte[1000]), destination, 1000, Ct));
        Assert.Equal(1000, destination.Length);
    }

    [Fact]
    public async Task One_byte_past_the_cap_is_refused_without_reading_a_stream_that_knows_its_length()
    {
        var source = new CountingStream(new MemoryStream(new byte[1001]), seekable: true);

        Assert.False(await BoundedCopy.TryCopyAsync(source, Stream.Null, 1000, Ct));
        Assert.Equal(0, source.BytesRead);
    }

    [Fact]
    public async Task A_stream_of_unknown_length_is_abandoned_at_the_first_chunk_past_the_cap()
    {
        // 64 MB on offer and a 1 MB cap: the copy reads the cap and the one byte that proves it was passed.
        var source = new CountingStream(new MemoryStream(new byte[64 * 1024 * 1024]), seekable: false);
        using var destination = new MemoryStream();

        Assert.False(await BoundedCopy.TryCopyAsync(source, destination, 1024 * 1024, Ct));
        Assert.Equal((1024 * 1024) + 1, source.BytesRead);
        Assert.True(destination.Length <= 1024 * 1024);
    }

    private sealed class CountingStream(Stream inner, bool seekable) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => seekable;

        public override bool CanWrite => false;

        public override long Length => seekable ? inner.Length : throw new NotSupportedException();

        public override long Position
        {
            get => seekable ? inner.Position : throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
