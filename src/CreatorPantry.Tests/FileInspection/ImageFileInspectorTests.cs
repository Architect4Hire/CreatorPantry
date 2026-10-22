using System.Text;
using CreatorPantry.Domain.Managers.FileInspection;
using CreatorPantry.Tests.Brand;
using CreatorPantry.Tests.Media;

namespace CreatorPantry.Tests.FileInspection;

/// <summary>
/// The one structural reading of a picture every upload path shares (15.2): what it accepts, and the polyglots,
/// trailers, damage and oversize it refuses.
/// </summary>
public sealed class ImageFileInspectorTests
{
    private const long Cap = 50_000_000;

    private static ImageFileInspection Inspect(byte[] bytes) => ImageFileInspector.Inspect(bytes, Cap);

    public static TheoryData<string, byte[], string> Whole() => new()
    {
        { "png", BrandSourceSampleFiles.Png(), ImageFileInspector.PngMediaType },
        { "jpeg", BrandSourceSampleFiles.Jpeg(), ImageFileInspector.JpegMediaType },
        { "webp", BrandSourceSampleFiles.Webp(), ImageFileInspector.WebpMediaType },
        { "gif", BrandSourceSampleFiles.Gif(), ImageFileInspector.GifMediaType },
    };

    [Theory]
    [MemberData(nameof(Whole))]
    public void A_whole_image_is_accepted_as_what_its_bytes_are(string format, byte[] bytes, string mediaType)
    {
        var inspection = Inspect(bytes);

        Assert.Equal(ImageFileInspectionOutcome.Accepted, inspection.Outcome);
        Assert.Equal(mediaType, inspection.MediaType);
        Assert.Equal(1, inspection.FrameCount);
        Assert.False(inspection.HasLocationMetadata);
        Assert.Contains(format, mediaType, StringComparison.Ordinal);
    }

    // ---- polyglots and trailing data ----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Whole))]
    public void A_zip_appended_to_an_image_is_refused(string format, byte[] bytes, string mediaType)
    {
        var polyglot = HostileSamples.AppendZip(bytes);

        // The attack is real: an ordinary reader opens it as an archive.
        Assert.True(HostileSamples.OpensAsZip(polyglot), format);
        Assert.Equal(ImageFileInspectionOutcome.TrailingData, Inspect(polyglot).Outcome);
        Assert.Equal(mediaType, ImageFileInspector.MediaTypeOf(polyglot));
    }

    [Theory]
    [MemberData(nameof(Whole))]
    public void Markup_after_an_image_is_refused(string format, byte[] bytes, string mediaType)
    {
        _ = mediaType;
        var polyglot = HostileSamples.Concat(bytes, Encoding.ASCII.GetBytes("<html><script>alert(1)</script></html>"));

        Assert.True(Inspect(polyglot).Outcome == ImageFileInspectionOutcome.TrailingData, format);
    }

    [Fact]
    public void A_zip_hidden_in_a_valid_chunk_with_its_comment_over_the_end_is_refused()
    {
        var polyglot = HostileSamples.PngHidingZip();

        // Both at once: a PNG that walks to IEND, and an archive a ZIP reader opens.
        Assert.True(HostileSamples.OpensAsZip(polyglot));
        Assert.Equal(ImageFileInspectionOutcome.TrailingData, Inspect(polyglot).Outcome);
    }

    [Fact]
    public void An_html_page_named_and_labelled_as_a_picture_is_not_one()
    {
        Assert.Equal(
            ImageFileInspectionOutcome.Unsupported,
            Inspect(Encoding.ASCII.GetBytes("<!doctype html><svg onload=alert(1)>")).Outcome);
    }

    [Fact]
    public void A_jpeg_followed_by_whole_jpegs_is_accepted_as_the_first()
    {
        // A gain map or a depth map: what current phones write after the picture itself.
        var withGainMap = HostileSamples.Concat(
            BrandSourceSampleFiles.Jpeg(width: 40, height: 30), BrandSourceSampleFiles.Jpeg(width: 10, height: 8));

        var inspection = Inspect(withGainMap);

        Assert.Equal(ImageFileInspectionOutcome.Accepted, inspection.Outcome);
        Assert.Equal(40, inspection.Width);
        Assert.Equal(30, inspection.Height);
    }

    [Fact]
    public void A_motion_photo_video_after_a_jpeg_is_refused()
    {
        var motionPhoto = HostileSamples.Concat(BrandSourceSampleFiles.Jpeg(), HostileSamples.Mp4Box());

        Assert.Equal(ImageFileInspectionOutcome.TrailingData, Inspect(motionPhoto).Outcome);
    }

    [Fact]
    public void A_damaged_jpeg_after_a_jpeg_is_corrupt()
    {
        var jpeg = BrandSourceSampleFiles.Jpeg();
        var damaged = HostileSamples.Concat(jpeg, jpeg[..^2]);

        Assert.Equal(ImageFileInspectionOutcome.Corrupt, Inspect(damaged).Outcome);
    }

    [Fact]
    public void A_webp_whose_riff_ends_before_the_file_is_refused()
    {
        Assert.Equal(
            ImageFileInspectionOutcome.TrailingData,
            Inspect(HostileSamples.Concat(BrandSourceSampleFiles.Webp(), [0x00, 0x00])).Outcome);
    }

    [Fact]
    public void A_simple_webp_carrying_a_second_chunk_is_corrupt()
    {
        // VP8L alone is the whole of a simple file; anything after it rides inside the RIFF unaccounted for.
        var bytes = new List<byte>();
        bytes.AddRange("RIFF"u8);
        bytes.AddRange(BitConverter.GetBytes(4 + 14 + 12));
        bytes.AddRange("WEBP"u8);
        bytes.AddRange("VP8L"u8);
        bytes.AddRange(BitConverter.GetBytes(5));
        bytes.AddRange([.. HostileSamples.Vp8l(), 0x00]);
        bytes.AddRange("zIPp"u8);
        bytes.AddRange(BitConverter.GetBytes(4));
        bytes.AddRange("PK\u0003\u0004"u8);

        Assert.Equal(ImageFileInspectionOutcome.Corrupt, Inspect([.. bytes]).Outcome);
    }

    // ---- corrupt -------------------------------------------------------------------------------------------

    [Fact]
    public void A_png_chunk_whose_checksum_does_not_hold_is_corrupt()
    {
        var png = BrandSourceSampleFiles.Png();

        // Inside the IDAT data: the header still reads, and only the checksum tells.
        png[^20] ^= 0xFF;

        Assert.Equal(ImageFileInspectionOutcome.Corrupt, Inspect(png).Outcome);
    }

    [Fact]
    public void A_png_with_no_image_data_is_corrupt()
    {
        Assert.Equal(
            ImageFileInspectionOutcome.Corrupt,
            Inspect(new PngSampleFile { Width = 2, Height = 2, OmitData = true }.Build()).Outcome);
    }

    [Fact]
    public void A_png_with_a_second_header_is_corrupt()
    {
        var header = BrandSourceSampleFiles.Png()[16..29];
        var png = new PngSampleFile
        {
            Width = 4, Height = 3, ColourType = 2, Samples = new byte[36], BeforeData = [("IHDR", header)],
        }.Build();

        Assert.Equal(ImageFileInspectionOutcome.Corrupt, Inspect(png).Outcome);
    }

    [Theory]
    [InlineData((byte)16, (byte)3)]
    [InlineData((byte)8, (byte)5)]
    [InlineData((byte)3, (byte)2)]
    public void A_png_header_no_encoder_can_write_is_corrupt(byte depth, byte colourType)
    {
        var png = new PngSampleFile
        {
            Width = 1, Height = 1, Depth = depth, ColourType = colourType, CompressedData = PngSampleFile.Compress([0]),
        }.Build();

        Assert.Equal(ImageFileInspectionOutcome.Corrupt, Inspect(png).Outcome);
    }

    [Fact]
    public void A_jpeg_that_never_reaches_its_end_marker_is_corrupt()
    {
        Assert.Equal(ImageFileInspectionOutcome.Corrupt, Inspect(BrandSourceSampleFiles.Jpeg()[..^2]).Outcome);
    }

    [Fact]
    public void A_jpeg_with_a_frame_and_no_scan_is_corrupt()
    {
        // The header-only file the old inspector accepted: SOI, a frame header, EOI.
        byte[] headerOnly = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x03, 0x00, 0x04, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xD9];

        Assert.Equal(ImageFileInspectionOutcome.Corrupt, Inspect(headerOnly).Outcome);
    }

    [Fact]
    public void A_gif_that_runs_out_before_its_trailer_is_corrupt()
    {
        Assert.Equal(ImageFileInspectionOutcome.Corrupt, Inspect(BrandSourceSampleFiles.Gif()[..^1]).Outcome);
    }

    [Fact]
    public void A_gif_with_no_frame_is_corrupt()
    {
        Assert.Equal(ImageFileInspectionOutcome.Corrupt, Inspect(BrandSourceSampleFiles.Gif(frames: 0)).Outcome);
    }

    [Fact]
    public void A_webp_with_a_canvas_and_no_picture_is_corrupt()
    {
        Assert.Equal(ImageFileInspectionOutcome.Corrupt, Inspect(HostileSamples.Webp(0)).Outcome);
    }

    // ---- oversized -----------------------------------------------------------------------------------------

    [Fact]
    public void A_few_bytes_declaring_a_vast_canvas_are_refused_as_too_large_before_being_walked()
    {
        // Nothing after the header holds together, and the answer is still about the size it claims.
        var claim = BrandSourceSampleFiles.Png(width: 60_000, height: 60_000)[..33];

        Assert.Equal(ImageFileInspectionOutcome.TooLarge, Inspect(claim).Outcome);
    }

    [Fact]
    public void A_gain_map_past_the_cap_is_refused_like_the_picture_would_be()
    {
        var withHugeTail = HostileSamples.Concat(
            BrandSourceSampleFiles.Jpeg(), BrandSourceSampleFiles.Jpeg(width: 60_000, height: 60_000));

        Assert.Equal(ImageFileInspectionOutcome.TooLarge, Inspect(withHugeTail).Outcome);
    }

    // ---- frames --------------------------------------------------------------------------------------------

    [Fact]
    public void An_apng_reports_the_frames_its_control_chunk_declares()
    {
        var apng = new PngSampleFile
        {
            Width = 2, Height = 2, ColourType = 2, Samples = new byte[12],
            BeforeData = [("acTL", [0, 0, 0, 3, 0, 0, 0, 0])],
        }.Build();

        var inspection = Inspect(apng);

        Assert.Equal(ImageFileInspectionOutcome.Accepted, inspection.Outcome);
        Assert.Equal(3, inspection.FrameCount);
        Assert.True(inspection.IsAnimated);
    }

    [Fact]
    public void An_animated_webp_counts_its_frame_chunks()
    {
        var frame = new byte[16];
        var webp = HostileSamples.Webp(0x02, ("ANIM", new byte[6]), ("ANMF", frame), ("ANMF", frame));

        var inspection = Inspect(webp);

        Assert.Equal(ImageFileInspectionOutcome.Accepted, inspection.Outcome);
        Assert.Equal(2, inspection.FrameCount);
    }

    [Fact]
    public void A_gif_counts_its_image_descriptors()
    {
        Assert.Equal(4, Inspect(BrandSourceSampleFiles.Gif(frames: 4)).FrameCount);
    }

    // ---- location metadata ---------------------------------------------------------------------------------

    [Fact]
    public void A_jpeg_with_a_gps_position_in_its_exif_is_reported_and_still_accepted()
    {
        var inspection = Inspect(HostileSamples.JpegWithExif(withPosition: true));

        Assert.Equal(ImageFileInspectionOutcome.Accepted, inspection.Outcome);
        Assert.True(inspection.HasLocationMetadata);
    }

    [Fact]
    public void A_position_in_an_appended_gain_map_is_still_a_position_in_the_file()
    {
        var withGainMap = HostileSamples.Concat(BrandSourceSampleFiles.Jpeg(), HostileSamples.JpegWithExif(withPosition: true));

        var inspection = Inspect(withGainMap);

        Assert.Equal(ImageFileInspectionOutcome.Accepted, inspection.Outcome);
        Assert.True(inspection.HasLocationMetadata);
    }

    [Fact]
    public void A_gps_directory_with_no_position_in_it_is_not_a_location()
    {
        Assert.False(Inspect(HostileSamples.JpegWithExif(withPosition: false)).HasLocationMetadata);
    }

    [Fact]
    public void A_position_in_xmp_is_reported()
    {
        Assert.True(Inspect(HostileSamples.JpegWithXmpPosition()).HasLocationMetadata);
    }

    [Fact]
    public void A_png_exif_chunk_with_a_position_is_reported()
    {
        var png = new PngSampleFile
        {
            Width = 2, Height = 2, ColourType = 2, Samples = new byte[12],
            BeforeData = [("eXIf", HostileSamples.Tiff(withPosition: true))],
        }.Build();

        Assert.True(Inspect(png).HasLocationMetadata);
    }

    [Fact]
    public void A_webp_exif_chunk_with_a_position_is_reported()
    {
        var webp = HostileSamples.Webp(0x08, ("VP8L", HostileSamples.Vp8l()), ("EXIF", HostileSamples.Tiff(withPosition: true)));

        var inspection = Inspect(webp);

        Assert.Equal(ImageFileInspectionOutcome.Accepted, inspection.Outcome);
        Assert.True(inspection.HasLocationMetadata);
    }

    [Fact]
    public void Malformed_exif_is_no_location_and_no_reason_to_refuse_the_picture()
    {
        var garbled = HostileSamples.Tiff(withPosition: true);

        // The GPS directory pointer now aims past the end of the structure.
        garbled[18] = 0xF0;

        var inspection = Inspect(HostileSamples.JpegWithSegment(0xE1, [.. "Exif\0\0"u8, .. garbled]));

        Assert.Equal(ImageFileInspectionOutcome.Accepted, inspection.Outcome);
        Assert.False(inspection.HasLocationMetadata);
    }

    // ---- signatures ----------------------------------------------------------------------------------------

    [Fact]
    public void The_signature_dispatch_names_nothing_it_cannot_read()
    {
        Assert.Null(ImageFileInspector.MediaTypeOf(BrandSourceSampleFiles.Executable()));
        Assert.Null(ImageFileInspector.MediaTypeOf(BrandSourceSampleFiles.Pdf()));
        Assert.Null(ImageFileInspector.MediaTypeOf([0xFF, 0xD8, 0x00]));
        Assert.Equal(ImageFileInspectionOutcome.Empty, Inspect([]).Outcome);
    }
}
