using System.Text.Json;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// That the managed encoder writes a conformant baseline JPEG, and that the picture in it is the one it
/// was given.
/// </summary>
/// <remarks>
/// Everything here reads the output back through <see cref="JpegSampleReader"/>, a strict decoder that
/// shares nothing with the encoder. What that cannot prove is that a real browser agrees; the last test
/// writes the fixtures that check was run against (B-28, AF.5.3) and does nothing in an ordinary run.
/// </remarks>
public sealed class JpegEncoderTests
{
    /// <summary>Names a directory to write the browser fixtures into. Unset in every ordinary run.</summary>
    private const string FixtureDirectoryVariable = "JPEG_BROWSER_FIXTURES";

    public static TheoryData<int, int, JpegChromaSubsampling> AwkwardSizes()
    {
        var sizes = new TheoryData<int, int, JpegChromaSubsampling>();

        // Smaller than a block, exactly one, one over; the same around the sixteen a quartered unit spans;
        // and single rows and columns, where every block is almost all padding.
        foreach (var (width, height) in new[] { (1, 1), (7, 7), (8, 8), (9, 9), (15, 17), (16, 16), (17, 31), (1, 33), (33, 1) })
        {
            sizes.Add(width, height, JpegChromaSubsampling.Quarter);
            sizes.Add(width, height, JpegChromaSubsampling.Full);
        }

        return sizes;
    }

    [Theory]
    [InlineData(JpegChromaSubsampling.Quarter, 2)]
    [InlineData(JpegChromaSubsampling.Full, 1)]
    public void The_file_is_a_baseline_jfif_with_its_segments_in_order(JpegChromaSubsampling subsampling, int sampling)
    {
        var jpeg = JpegSampleReader.Read(Encoded(Textured(40, 24), 82, subsampling));

        Assert.Equal(["SOI", "APP0", "DQT", "DQT", "SOF0", "DHT", "DHT", "DHT", "DHT", "SOS", "EOI"], jpeg.Segments);

        // JFIF 1.01, no density units, square pixels, no thumbnail.
        Assert.Equal([.. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0], jpeg.Application);

        Assert.Equal(8, jpeg.Precision);
        Assert.Equal((40, 24), (jpeg.Width, jpeg.Height));
        Assert.Equal(
            [(1, sampling, sampling, 0), (2, 1, 1, 1), (3, 1, 1, 1)],
            jpeg.Components);
    }

    [Fact]
    public void Quality_scales_the_published_quantisation_tables_the_usual_way()
    {
        byte[] Table(int quality, int id) => JpegSampleReader.Read(Encoded(Flat(8, 8, 9, 9, 9), quality)).Quantisation[id];

        // Fifty is the standard's tables as printed: the first row of each, and the last entry.
        Assert.Equal([16, 11, 10, 16, 24, 40, 51, 61], Table(50, 0)[..8]);
        Assert.Equal([17, 18, 24, 47, 99, 99, 99, 99], Table(50, 1)[..8]);
        Assert.Equal(99, Table(50, 0)[63]);

        // One hundred keeps everything; twenty-five doubles every step; one saturates.
        Assert.All(Table(100, 0).Concat(Table(100, 1)), step => Assert.Equal(1, step));
        Assert.Equal([32, 22, 20, 32, 48, 80, 102, 122], Table(25, 0)[..8]);
        Assert.All(Table(1, 0), step => Assert.Equal(255, step));
    }

    [Fact]
    public void The_huffman_tables_cover_every_symbol_a_baseline_stream_can_need_exactly_once()
    {
        var tables = JpegSampleReader.Read(Encoded(Flat(8, 8, 9, 9, 9), 82)).Huffman;

        Assert.Equal([0x00, 0x01, 0x10, 0x11], tables.Keys.Order());

        // DC: a size from 0 to 11. AC: end-of-block, a run of sixteen zeroes, and every run with every size.
        var everyAcSymbol = Enumerable.Range(0, 16)
            .SelectMany(run => Enumerable.Range(1, 10).Select(size => (byte)((run << 4) | size)))
            .Append((byte)0x00)
            .Append((byte)0xF0)
            .Order();

        Assert.Equal(Enumerable.Range(0, 12).Select(size => (byte)size), tables[0x00].Symbols.Order());
        Assert.Equal(Enumerable.Range(0, 12).Select(size => (byte)size), tables[0x01].Symbols.Order());
        Assert.Equal(everyAcSymbol, tables[0x10].Symbols.Order());
        Assert.Equal(everyAcSymbol, tables[0x11].Symbols.Order());
    }

    public static TheoryData<byte, byte, byte> Colours() => new()
    {
        { 0, 0, 0 }, { 255, 255, 255 }, { 128, 128, 128 }, { 255, 0, 0 }, { 0, 255, 0 }, { 0, 0, 255 }, { 37, 150, 190 },
    };

    [Theory]
    [MemberData(nameof(Colours))]
    public void A_flat_colour_comes_back_as_that_colour(byte red, byte green, byte blue)
    {
        foreach (var subsampling in Enum.GetValues<JpegChromaSubsampling>())
        {
            var jpeg = JpegSampleReader.Read(Encoded(Flat(24, 24, red, green, blue), 82, subsampling));

            AssertFlat(jpeg, red, green, blue);
        }
    }

    [Theory]
    [MemberData(nameof(AwkwardSizes))]
    public void A_size_that_is_not_whole_blocks_keeps_its_size_and_its_edges(
        int width, int height, JpegChromaSubsampling subsampling)
    {
        // Saturated red, so padding with anything but the edge's own colour would ring into the last
        // visible row and column.
        var jpeg = JpegSampleReader.Read(Encoded(Flat(width, height, 255, 0, 0), 82, subsampling));

        Assert.Equal((width, height), (jpeg.Width, jpeg.Height));
        AssertFlat(jpeg, 255, 0, 0);
    }

    [Theory]
    [InlineData(JpegChromaSubsampling.Quarter, 82, 30)]
    [InlineData(JpegChromaSubsampling.Full, 82, 32)]
    [InlineData(JpegChromaSubsampling.Full, 100, 50)]
    public void A_detailed_picture_comes_back_close_to_itself(JpegChromaSubsampling subsampling, int quality, double floor)
    {
        var source = Textured(96, 72);
        var jpeg = JpegSampleReader.Read(Encoded(source, quality, subsampling));

        Assert.True(Psnr(source, jpeg) >= floor, $"PSNR {Psnr(source, jpeg):F1} dB is below {floor}");
    }

    [Fact]
    public void A_higher_quality_is_a_larger_file()
    {
        var source = Textured(96, 72);
        var sizes = Enumerable.Range(1, 10).Select(step => Encoded(source, step * 10).Length).ToArray();

        Assert.Equal(sizes.Order(), sizes);
        Assert.Equal(sizes.Length, sizes.Distinct().Count());
    }

    [Fact]
    public void The_same_picture_and_settings_give_the_same_bytes()
    {
        Assert.Equal(Encoded(Textured(50, 30), 78), Encoded(Textured(50, 30), 78));
    }

    [Fact]
    public void A_picture_with_any_transparency_is_reported_and_not_flattened()
    {
        var pixels = new PixelBuffer(2, 1, [10, 20, 30, 255, 40, 50, 60, 254], hasTransparency: true);

        var result = JpegEncoder.Encode(pixels, 82, JpegChromaSubsampling.Quarter, TestContext.Current.CancellationToken);

        Assert.Equal(JpegEncodeOutcome.HasTransparency, result.Outcome);
        Assert.Null(result.Bytes);
    }

    [Fact]
    public void A_side_longer_than_a_frame_header_can_state_is_too_large()
    {
        var widest = new PixelBuffer(65_535, 1, new byte[65_535 * 4], hasTransparency: false);
        var tooWide = new PixelBuffer(65_536, 1, new byte[65_536 * 4], hasTransparency: false);

        Assert.Equal(65_535, JpegSampleReader.Read(Encoded(widest, 50)).Width);
        Assert.Equal(
            JpegEncodeOutcome.TooLarge,
            JpegEncoder.Encode(tooWide, 50, JpegChromaSubsampling.Quarter, TestContext.Current.CancellationToken).Outcome);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-5)]
    public void A_quality_outside_one_to_a_hundred_is_a_caller_error(int quality)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegEncoder.Encode(
            Flat(1, 1, 0, 0, 0), quality, JpegChromaSubsampling.Quarter, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Cancellation_stops_it()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => JpegEncoder.Encode(Flat(8, 8, 1, 2, 3), 82, JpegChromaSubsampling.Quarter, cancelled.Token));
    }

    [Fact]
    public void A_png_goes_through_the_decoder_the_downscaler_and_the_encoder_to_a_smaller_jpeg()
    {
        var source = Textured(64, 48);
        var png = new PngSampleFile
        {
            Width = 64,
            Height = 48,
            Samples = source.Rgba.ToArray(),
            Filter = row => (byte)(row % 5),
        }.Build();

        var decoded = PngDecoder.Decode(png, TestContext.Current.CancellationToken).Pixels!;
        var fitted = AreaAverageDownscaler.Downscale(decoded, 32, 32, TestContext.Current.CancellationToken);
        var jpeg = JpegSampleReader.Read(Encoded(fitted, 78));

        Assert.Equal((32, 24), (jpeg.Width, jpeg.Height));
        Assert.True(Psnr(fitted, jpeg) >= 26, $"PSNR {Psnr(fitted, jpeg):F1} dB");
    }

    /// <summary>
    /// Writes the files the browser check opens, when <c>JPEG_BROWSER_FIXTURES</c> names a directory.
    /// </summary>
    /// <remarks>
    /// The check itself is not part of this suite: it needs a browser. Each fixture is a gradient a page can
    /// recompute for itself — red rising across, green rising down, blue constant — so the page needs no
    /// expected pixels from here, only how far this suite's own decoder landed from that gradient. If the directory holds a <c>source.png</c>, that is also taken through
    /// the whole path to both of B-28's renditions, which is how a real generated picture gets checked
    /// without one being committed.
    /// </remarks>
    [Fact]
    public void Browser_fixtures_are_written_when_a_directory_is_named()
    {
        if (Environment.GetEnvironmentVariable(FixtureDirectoryVariable) is not { Length: > 0 } directory)
        {
            return;
        }

        var manifest = new List<object>();

        foreach (var row in AwkwardSizes().Concat([new(96, 72, JpegChromaSubsampling.Quarter), new(96, 72, JpegChromaSubsampling.Full)]))
        {
            var (width, height, subsampling) = row.Data;

            foreach (var quality in new[] { 78, 82 })
            {
                var name = $"gradient-{width}x{height}-{subsampling}-q{quality}.jpg".ToLowerInvariant();
                var source = Gradient(width, height);
                var bytes = Encoded(source, quality, subsampling);
                File.WriteAllBytes(Path.Combine(directory, name), bytes);

                // How far this suite's own decoder lands from the source, for the page to compare a
                // browser's answer with: the two should miss the source by about the same amount.
                var referenceError = MeanError(source, JpegSampleReader.Read(bytes));
                manifest.Add(new { file = name, width, height, pattern = "gradient", referenceError });
            }
        }

        var real = Path.Combine(directory, "source.png");

        if (File.Exists(real))
        {
            var decoded = PngDecoder.Decode(File.ReadAllBytes(real), TestContext.Current.CancellationToken).Pixels!;

            foreach (var (name, edge, quality) in new[] { ("real-web.jpg", 1600, 82), ("real-thumbnail.jpg", 480, 78) })
            {
                var fitted = AreaAverageDownscaler.Downscale(decoded, edge, edge, TestContext.Current.CancellationToken);
                File.WriteAllBytes(Path.Combine(directory, name), Encoded(fitted, quality));
                manifest.Add(new { file = name, width = fitted.Width, height = fitted.Height, pattern = "source" });
            }
        }

        File.WriteAllText(Path.Combine(directory, "fixtures.json"), JsonSerializer.Serialize(manifest));
    }

    private static byte[] Encoded(
        PixelBuffer pixels, int quality, JpegChromaSubsampling subsampling = JpegChromaSubsampling.Quarter)
    {
        var result = JpegEncoder.Encode(pixels, quality, subsampling, TestContext.Current.CancellationToken);

        Assert.Equal(JpegEncodeOutcome.Encoded, result.Outcome);
        return result.Bytes!;
    }

    /// <summary>Every pixel within three levels of the colour: the slack a lossy round trip is owed.</summary>
    private static void AssertFlat(JpegSampleReader jpeg, byte red, byte green, byte blue)
    {
        for (var y = 0; y < jpeg.Height; y++)
        {
            for (var x = 0; x < jpeg.Width; x++)
            {
                var (r, g, b) = jpeg.Pixel(x, y);

                Assert.True(
                    Math.Abs(r - red) <= 3 && Math.Abs(g - green) <= 3 && Math.Abs(b - blue) <= 3,
                    $"({x}, {y}) of {jpeg.Width}×{jpeg.Height} is ({r}, {g}, {b}), not ({red}, {green}, {blue})");
            }
        }
    }

    private static double MeanError(PixelBuffer source, JpegSampleReader jpeg)
    {
        double total = 0;

        for (var i = 0; i < source.Width * source.Height; i++)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                total += Math.Abs(source.Rgba[(i * 4) + channel] - jpeg.Rgb[(i * 3) + channel]);
            }
        }

        return total / (source.Width * source.Height * 3);
    }

    private static double Psnr(PixelBuffer source, JpegSampleReader jpeg)
    {
        double squared = 0;

        for (var i = 0; i < source.Width * source.Height; i++)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                var difference = source.Rgba[(i * 4) + channel] - jpeg.Rgb[(i * 3) + channel];
                squared += difference * difference;
            }
        }

        return 10 * Math.Log10(255 * 255 / (squared / (source.Width * source.Height * 3)));
    }

    private static PixelBuffer Flat(int width, int height, byte red, byte green, byte blue) =>
        Picture(width, height, (_, _) => (red, green, blue));

    /// <summary>Red rising left to right, green rising top to bottom, blue constant.</summary>
    private static PixelBuffer Gradient(int width, int height) => Picture(
        width,
        height,
        (x, y) => ((byte)(x * 255 / Math.Max(1, width - 1)), (byte)(y * 255 / Math.Max(1, height - 1)), 128));

    /// <summary>
    /// Smooth shapes in each channel with fine detail over all three: enough in every frequency that
    /// quality changes the size, and nothing random, so a failure reproduces.
    /// </summary>
    /// <remarks>
    /// The fine detail is the same in every channel — brightness, not colour — which is how photographs
    /// are and what quartering the colour assumes.
    /// </remarks>
    private static PixelBuffer Textured(int width, int height) => Picture(
        width,
        height,
        (x, y) =>
        {
            var detail = (12 * Math.Sin((x + (3 * y)) / 2.0)) + (((x * 7) + (y * 13)) % 17) - 8;

            return (
                (byte)(128 + (70 * Math.Sin(x / 7.0)) + detail),
                (byte)(128 + (70 * Math.Cos(y / 5.0)) + detail),
                (byte)(128 + (70 * Math.Sin((x + y) / 9.0)) + detail));
        });

    private static PixelBuffer Picture(int width, int height, Func<int, int, (byte R, byte G, byte B)> colour)
    {
        var rgba = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (red, green, blue) = colour(x, y);
                var at = ((y * width) + x) * 4;
                rgba[at] = red;
                rgba[at + 1] = green;
                rgba[at + 2] = blue;
                rgba[at + 3] = byte.MaxValue;
            }
        }

        return new PixelBuffer(width, height, rgba, hasTransparency: false);
    }
}
