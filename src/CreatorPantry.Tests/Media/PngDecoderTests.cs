using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Tests.Brand;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// What the managed PNG decoder reads, what it refuses, and what a hostile file can make it spend.
/// </summary>
/// <remarks>
/// Known answers come from two places: <see cref="PngSampleFile"/>, which writes every colour type, depth
/// and filter this must read, and three files written by a different encoder altogether, so the decoder is
/// not only ever checked against the writer in the next file.
/// </remarks>
public sealed class PngDecoderTests
{
    private const int Columns = 3;
    private const int Rows = 5;

    /// <summary>Fifteen pixels with nothing regular about them, a third opaque and the rest not.</summary>
    private static readonly (byte R, byte G, byte B, byte A)[] Pixels = Enumerable.Range(0, Columns * Rows)
        .Select(i => (
            (byte)(((i * 37) + 11) % 256),
            (byte)(((i * 91) + 5) % 256),
            (byte)(((i * 53) + 200) % 256),
            i % 3 == 0 ? byte.MaxValue : (byte)((i * 67) % 256)))
        .ToArray();

    public static TheoryData<byte, byte> ReadableFormats() => new()
    {
        { 0, 8 }, { 0, 16 }, { 2, 8 }, { 2, 16 }, { 3, 8 }, { 4, 8 }, { 4, 16 }, { 6, 8 }, { 6, 16 },
    };

    [Theory]
    [MemberData(nameof(ReadableFormats))]
    public void Every_colour_type_and_depth_in_scope_decodes_to_the_pixels_that_were_written(byte colourType, byte depth)
    {
        // Five rows, one per filter type, so each colour type is read back through every filter.
        var result = PngDecoder.Decode(Sample(colourType, depth).Build(), TestContext.Current.CancellationToken);

        Assert.Equal(PngDecodeOutcome.Decoded, result.Outcome);
        Assert.Equal(Columns, result.Pixels!.Width);
        Assert.Equal(Rows, result.Pixels.Height);
        Assert.Equal(Expected(colourType), result.Pixels.Rgba.ToArray());

        // Only the colour types that can carry alpha report any.
        Assert.Equal(colourType is 3 or 4 or 6, result.Pixels.HasTransparency);
    }

    public static TheoryData<string, string, bool> IndependentFiles() => new()
    {
        { "truecolour", GdiTruecolour, false },
        { "truecolour with alpha", GdiTruecolourAlpha, true },
        { "palette", GdiPalette, false },
    };

    [Theory]
    [MemberData(nameof(IndependentFiles))]
    public void A_file_written_by_another_encoder_decodes_to_the_pixels_it_was_given(
        string kind, string base64, bool transparent)
    {
        var result = PngDecoder.Decode(Convert.FromBase64String(base64), TestContext.Current.CancellationToken);

        Assert.Equal(PngDecodeOutcome.Decoded, result.Outcome);
        Assert.Equal(5, result.Pixels!.Width);
        Assert.Equal(4, result.Pixels.Height);
        Assert.Equal(transparent, result.Pixels.HasTransparency);

        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 5; x++)
            {
                var index = (x + (y * 5)) % 12;

                // The formulas the pictures were drawn with, restated: see the constants below.
                byte[] expected = kind == "palette"
                    ? [(byte)(index * 20), (byte)(255 - (index * 20)), (byte)(index * 7), 255]
                    : [(byte)(x * 50), (byte)(y * 60), (byte)((x + y) * 25), transparent ? (byte)((x * 40) + (y * 25) + 15) : (byte)255];

                Assert.Equal(expected, result.Pixels.Rgba.Slice(((y * 5) + x) * 4, 4).ToArray());
            }
        }
    }

    [Fact]
    public void A_colour_key_makes_exactly_the_keyed_colour_transparent()
    {
        var greyscale = new PngSampleFile
        {
            Width = 3, ColourType = 0, Samples = [40, 41, 40], Transparency = [0, 40],
        };
        var truecolour = new PngSampleFile
        {
            Width = 2, ColourType = 2, Samples = [1, 2, 3, 1, 2, 4], Transparency = [0, 1, 0, 2, 0, 3],
        };

        Assert.Equal(
            [40, 40, 40, 0, 41, 41, 41, 255, 40, 40, 40, 0],
            Decoded(greyscale).Rgba.ToArray());
        Assert.Equal(
            [1, 2, 3, 0, 1, 2, 4, 255],
            Decoded(truecolour).Rgba.ToArray());
    }

    [Fact]
    public void A_sixteen_bit_colour_key_is_matched_on_both_bytes_before_the_low_one_is_dropped()
    {
        // Two pixels that become the same eight-bit grey. Only the first is the keyed sixteen-bit value.
        var sample = new PngSampleFile
        {
            Width = 2, ColourType = 0, Depth = 16, Samples = [0x40, 0x01, 0x40, 0x02], Transparency = [0x40, 0x01],
        };

        Assert.Equal([0x40, 0x40, 0x40, 0, 0x40, 0x40, 0x40, 255], Decoded(sample).Rgba.ToArray());
    }

    [Fact]
    public void Compressed_data_split_across_chunks_is_one_stream()
    {
        var whole = Sample(colourType: 6, depth: 8);
        var split = Sample(colourType: 6, depth: 8, dataChunks: 7);

        Assert.True(split.Build().Length > whole.Build().Length);
        Assert.Equal(Decoded(whole).Rgba.ToArray(), Decoded(split).Rgba.ToArray());
    }

    [Fact]
    public void A_chunk_it_does_not_know_and_may_skip_is_skipped()
    {
        var sample = Sample(colourType: 2, depth: 8, beforeData: [("tEXt", "Comment\0a note"u8.ToArray())]);

        Assert.Equal(Expected(colourType: 2), Decoded(sample).Rgba.ToArray());
    }

    public static TheoryData<string, PngSampleFile> UnreadableFormats() => new()
    {
        { "interlaced", Sample(6, 8, interlace: 1) },
        { "four bits a sample", new PngSampleFile { ColourType = 0, Depth = 4, Samples = [0] } },
        { "a sixteen-bit palette", new PngSampleFile { ColourType = 3, Depth = 16, Palette = [1, 2, 3], Samples = [0, 0] } },
        { "a colour type the format does not define", new PngSampleFile { ColourType = 5, Samples = [0, 0, 0, 0] } },
        { "a chunk it may not skip", Sample(6, 8, beforeData: [("CpXx", [1, 2, 3])]) },
    };

    [Theory]
    [MemberData(nameof(UnreadableFormats))]
    public void A_valid_file_of_a_kind_out_of_scope_is_refused_by_name(string kind, PngSampleFile sample)
    {
        var result = PngDecoder.Decode(sample.Build(), TestContext.Current.CancellationToken);

        Assert.True(result.Outcome == PngDecodeOutcome.Unsupported, kind);
        Assert.Null(result.Pixels);
    }

    public static TheoryData<string, PngSampleFile> BrokenFiles()
    {
        var sound = Sample(6, 8);
        var scanlines = sound.FilteredScanlines();
        var rowLength = scanlines.Length / Rows;

        return new()
        {
            { "a filter byte the format does not define", Sample(6, 8, scanlines: [9, .. scanlines[1..]]) },
            { "a row short", Sample(6, 8, scanlines: scanlines[..^rowLength]) },
            { "a byte short", Sample(6, 8, scanlines: scanlines[..^1]) },
            { "a row too many", Sample(6, 8, scanlines: [.. scanlines, .. scanlines[..rowLength]]) },
            { "a byte too many", Sample(6, 8, scanlines: [.. scanlines, 0]) },
            { "data that is not compressed data", Sample(6, 8, compressed: [0x78, 0x9C, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]) },
            { "no data at all", new PngSampleFile { OmitData = true } },
            { "a chunk between the data chunks", Sample(6, 8, dataChunks: 2, betweenData: ("tEXt", "a\0b"u8.ToArray())) },
            { "a palette index past the palette", new PngSampleFile { ColourType = 3, Palette = [1, 2, 3, 4, 5, 6], Samples = [2] } },
            { "a palette picture with no palette", new PngSampleFile { ColourType = 3, Samples = [0] } },
            { "a palette that is not whole colours", new PngSampleFile { ColourType = 3, Palette = [1, 2, 3, 4], Samples = [0] } },
            { "more palette alphas than palette entries", new PngSampleFile { ColourType = 3, Palette = [1, 2, 3], Transparency = [9, 9], Samples = [0] } },
            { "a colour key on a picture that has alpha", new PngSampleFile { Samples = [1, 2, 3, 4], Transparency = [0, 1] } },
            { "a colour key of the wrong size", new PngSampleFile { ColourType = 2, Samples = [1, 2, 3], Transparency = [0, 1] } },
            { "a second header", Sample(6, 8, beforeData: [("IHDR", new byte[13])]) },
        };
    }

    [Theory]
    [MemberData(nameof(BrokenFiles))]
    public void A_file_whose_checksums_hold_and_whose_contents_do_not_is_corrupt(string kind, PngSampleFile sample)
    {
        var result = PngDecoder.Decode(sample.Build(), TestContext.Current.CancellationToken);

        Assert.True(result.Outcome == PngDecodeOutcome.Corrupt, kind);
        Assert.Null(result.Pixels);
    }

    [Fact]
    public void A_file_cut_off_anywhere_is_a_result_and_never_a_picture()
    {
        var whole = Sample(6, 8, dataChunks: 3, beforeData: [("tEXt", "k\0v"u8.ToArray())]).Build();

        for (var length = 0; length < whole.Length; length++)
        {
            var outcome = PngDecoder.Decode(whole.AsMemory(0, length), TestContext.Current.CancellationToken).Outcome;

            // Short of the signature there is nothing to call a PNG; past it, it is a PNG that stopped.
            Assert.True(outcome == (length < 8 ? PngDecodeOutcome.NotPng : PngDecodeOutcome.Corrupt), $"{length} bytes: {outcome}");
        }
    }

    [Fact]
    public void A_single_damaged_byte_anywhere_is_caught()
    {
        var whole = Sample(6, 8, dataChunks: 2).Build();

        for (var at = 0; at < whole.Length; at++)
        {
            var damaged = (byte[])whole.Clone();
            damaged[at] ^= 0x10;

            // Every byte of a PNG is signature, length, type, data or checksum, and each of those is checked.
            Assert.NotEqual(
                PngDecodeOutcome.Decoded,
                PngDecoder.Decode(damaged, TestContext.Current.CancellationToken).Outcome);
        }
    }

    public static TheoryData<string, byte[]> NotPngs() => new()
    {
        { "nothing", [] },
        { "a JPEG", BrandSourceSampleFiles.Jpeg() },
        { "an executable", BrandSourceSampleFiles.Executable() },
    };

    [Theory]
    [MemberData(nameof(NotPngs))]
    public void Another_format_is_not_a_png_rather_than_a_broken_one(string kind, byte[] bytes)
    {
        Assert.True(PngDecoder.Decode(bytes, TestContext.Current.CancellationToken).Outcome == PngDecodeOutcome.NotPng, kind);
    }

    [Theory]
    [InlineData(5000u, 5000u)]
    [InlineData(60_000u, 60_000u)]
    [InlineData(16_385u, 1u)]
    [InlineData(1u, 16_385u)]
    [InlineData(uint.MaxValue, uint.MaxValue)]
    public void A_small_file_declaring_a_picture_too_big_is_refused_without_being_allocated(uint width, uint height)
    {
        var file = new PngSampleFile { Width = width, Height = height, CompressedData = [] }.Build();
        var before = GC.GetAllocatedBytesForCurrentThread();

        var result = PngDecoder.Decode(file, TestContext.Current.CancellationToken);

        Assert.Equal(PngDecodeOutcome.TooLarge, result.Outcome);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 64 * 1024);
    }

    [Fact]
    public void The_caps_are_inclusive()
    {
        // Exactly at the pixel cap the size is accepted, so what is wrong with this file is that it has no
        // data — proving the boundary without holding a 96 MB picture in a unit test.
        var atPixelCap = new PngSampleFile { Width = 6000, Height = 4000, OmitData = true }.Build();
        Assert.Equal(6000L * 4000, MediaPolicy.RenditionMaxPixels);
        Assert.Equal(PngDecodeOutcome.Corrupt, PngDecoder.Decode(atPixelCap, TestContext.Current.CancellationToken).Outcome);

        var atEdgeCap = new PngSampleFile
        {
            Width = MediaPolicy.RenditionMaxEdge, ColourType = 0, Samples = new byte[MediaPolicy.RenditionMaxEdge],
        };
        Assert.Equal(MediaPolicy.RenditionMaxEdge, Decoded(atEdgeCap).Width);
    }

    [Fact]
    public void A_file_over_the_byte_limit_is_refused_before_it_is_read()
    {
        var file = new byte[MediaPolicy.ImageMaxBytes + 1];
        Sample(6, 8).Build().CopyTo(file, 0);

        Assert.Equal(PngDecodeOutcome.TooLarge, PngDecoder.Decode(file, TestContext.Current.CancellationToken).Outcome);
    }

    [Fact]
    public void A_decompression_bomb_is_corrupt_after_the_pixels_it_declared_not_after_it_has_gone_off()
    {
        // One declared pixel, and 64 MB of zeroes behind it in about 64 KB of file.
        var bomb = new PngSampleFile
        {
            ColourType = 0,
            CompressedData = PngSampleFile.Compress(new byte[64 * 1024 * 1024]),
        }.Build();
        Assert.InRange(bomb.Length, 1, 256 * 1024);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = PngDecoder.Decode(bomb, TestContext.Current.CancellationToken);

        Assert.Equal(PngDecodeOutcome.Corrupt, result.Outcome);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1024 * 1024);
    }

    [Fact]
    public void Cancellation_is_the_one_thing_that_leaves_as_an_exception()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => PngDecoder.Decode(Sample(6, 8).Build(), cancelled.Token));
    }

    private static PixelBuffer Decoded(PngSampleFile sample)
    {
        var result = PngDecoder.Decode(sample.Build(), TestContext.Current.CancellationToken);

        Assert.Equal(PngDecodeOutcome.Decoded, result.Outcome);
        return result.Pixels!;
    }

    /// <summary>
    /// <see cref="Pixels"/> as a file of the given colour type and depth, each row behind a different filter.
    /// </summary>
    /// <remarks>
    /// A sixteen-bit sample is the eight-bit value with 0x5A beneath it, so a decoder that kept the low byte
    /// instead of the high one would return 0x5A everywhere and fail loudly.
    /// </remarks>
    private static PngSampleFile Sample(
        byte colourType,
        byte depth,
        byte interlace = 0,
        int dataChunks = 1,
        IReadOnlyList<(string Type, byte[] Data)>? beforeData = null,
        (string Type, byte[] Data)? betweenData = null,
        byte[]? scanlines = null,
        byte[]? compressed = null)
    {
        var samples = new List<byte>();

        void Add(byte value)
        {
            samples.Add(value);

            if (depth == 16)
            {
                samples.Add(0x5A);
            }
        }

        for (var i = 0; i < Pixels.Length; i++)
        {
            var (r, g, b, a) = Pixels[i];

            switch (colourType)
            {
                case 0: Add(r); break;
                case 2: Add(r); Add(g); Add(b); break;
                case 3: Add((byte)i); break;
                case 4: Add(r); Add(a); break;
                default: Add(r); Add(g); Add(b); Add(a); break;
            }
        }

        return new PngSampleFile
        {
            Width = Columns,
            Height = Rows,
            ColourType = colourType,
            Depth = depth,
            Interlace = interlace,
            Samples = [.. samples],
            Filter = row => (byte)row,
            Palette = colourType == 3 ? [.. Pixels.SelectMany(p => new[] { p.R, p.G, p.B })] : null,
            Transparency = colourType == 3 ? [.. Pixels.Select(p => p.A)] : null,
            DataChunks = dataChunks,
            BeforeData = beforeData ?? [],
            BetweenData = betweenData,
            Scanlines = scanlines,
            CompressedData = compressed,
        };
    }

    /// <summary>What <see cref="Pixels"/> can still be once a colour type has had its say.</summary>
    private static byte[] Expected(byte colourType) =>
    [
        .. Pixels.SelectMany(p => colourType switch
        {
            0 => new[] { p.R, p.R, p.R, byte.MaxValue },
            2 => [p.R, p.G, p.B, byte.MaxValue],
            4 => [p.R, p.R, p.R, p.A],
            _ => [p.R, p.G, p.B, p.A],
        }),
    ];

    // Three 5 × 4 pictures saved by GDI+ (System.Drawing's PNG encoder) on 2026-10-10, each carrying the
    // sRGB, gAMA and pHYs chunks that encoder adds. Truecolour: pixel (x, y) is (50x, 60y, 25(x + y)); with
    // alpha, 40x + 25y + 15. Palette: entry i is (20i, 255 - 20i, 7i) and pixel (x, y) is entry (x + 5y) mod 12.
    private const string GdiTruecolour =
        "iVBORw0KGgoAAAANSUhEUgAAAAUAAAAECAIAAADJUWIXAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7D"
        + "AcdvqGQAAAA9SURBVBhXBcExAQBBCASxsXE2tsYBNlYEDTaw8Q0ikPUJgHhGQx6GeAo5csIXDSVVujzVVwObWnt7dm6/Hw8gFa/B1+1F"
        + "AAAAAElFTkSuQmCC";

    private const string GdiTruecolourAlpha =
        "iVBORw0KGgoAAAANSUhEUgAAAAUAAAAECAYAAABGM/VAAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7D"
        + "AcdvqGQAAABdSURBVBhXBcERAoBQDAbg/wbxu0E8SSbxJE6SSTeYPEniZBwnTybdYNwNukbU9wFARyiDghaH7AltAJeemCZlMWc9k2sC"
        + "RiOZrGp6uNU7zV8gZKbQTaNeHv5ktO8HFLQgCSLQwE0AAAAASUVORK5CYII=";

    private static readonly string GdiPalette =
        "iVBORw0KGgoAAAANSUhEUgAAAAUAAAAECAMAAABx7QVyAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAMAUExURQD/ABTrByjX"
        + "DjzDFVCvHGSbI3iHKoxzMaBfOLRLP8g3RtwjTQ"
        + new string('A', 975)
        + "BjcsAwAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAAfSURBVBhXBcGFAQAACMCg2fX/vwKImhNZPewhinlkPQQWAF8T/Zj5AAAAAElFTkSu"
        + "QmCC";
}
