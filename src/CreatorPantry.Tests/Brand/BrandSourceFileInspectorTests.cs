using System.Security.Cryptography;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// What the inspector decides a file is from its bytes, and what it refuses: the format rules of the upload,
/// without a host around them.
/// </summary>
public sealed class BrandSourceFileInspectorTests
{
    private static async Task<BrandSourceInspection> InspectAsync(byte[] bytes, string fileName)
    {
        using var content = new MemoryStream(bytes);
        var inspection = await BrandSourceFileInspector.InspectAsync(content, fileName, TestContext.Current.CancellationToken);

        // Whatever it read, it leaves the stream where the scan and the store expect it.
        Assert.Equal(0, content.Position);
        return inspection;
    }

    public static TheoryData<string, string, string> Accepted() => new()
    {
        { "pdf", "guide.pdf", "application/pdf" },
        { "docx", "guide.docx", BrandSourceSampleFiles.DocxMediaType },
        { "png", "logo.png", "image/png" },
        { "jpeg", "photo.jpg", "image/jpeg" },
        { "jpeg", "photo.JPEG", "image/jpeg" },
        { "webp", "photo.webp", "image/webp" },
        { "gif", "loaf.gif", "image/gif" },
        { "gif-87a", "loaf.GIF", "image/gif" },
        { "text", "voice.md", "text/markdown" },
        { "text", "voice.markdown", "text/markdown" },
        { "text", "voice.txt", "text/plain" },
        { "text", "post.html", "text/html" },
        { "text", "post.htm", "text/html" },
    };

    private static byte[] Sample(string kind) => kind switch
    {
        "pdf" => BrandSourceSampleFiles.Pdf(),
        "docx" => BrandSourceSampleFiles.Docx(),
        "png" => BrandSourceSampleFiles.Png(),
        "jpeg" => BrandSourceSampleFiles.Jpeg(),
        "webp" => BrandSourceSampleFiles.Webp(),
        "gif" => BrandSourceSampleFiles.Gif(),
        "gif-87a" => BrandSourceSampleFiles.Gif(legacyHeader: true),
        "text" => BrandSourceSampleFiles.Text(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public async Task An_accepted_format_is_named_from_its_bytes_and_measured(string kind, string fileName, string mediaType)
    {
        var bytes = Sample(kind);

        var inspection = await InspectAsync(bytes, fileName);

        Assert.Equal(BrandSourceInspectionOutcome.Accepted, inspection.Outcome);
        Assert.Equal(mediaType, inspection.MediaType);
        Assert.Equal(bytes.Length, inspection.SizeBytes);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)), inspection.ContentChecksum);
    }

    [Theory]
    [InlineData("pdf", "guide.txt")]
    [InlineData("pdf", "guide.docx")]
    [InlineData("png", "logo.jpg")]
    [InlineData("jpeg", "photo.png")]
    [InlineData("docx", "guide.pdf")]
    [InlineData("webp", "photo")]
    public async Task A_binary_format_under_another_formats_name_is_refused(string kind, string fileName)
    {
        var inspection = await InspectAsync(Sample(kind), fileName);

        Assert.Equal(BrandSourceInspectionOutcome.NameMismatch, inspection.Outcome);
        Assert.Null(inspection.MediaType);
    }

    [Theory]
    [InlineData("malware.pdf")]
    [InlineData("malware.txt")]
    [InlineData("malware.png")]
    [InlineData("malware.exe")]
    public async Task An_executable_is_unsupported_whatever_it_is_called(string fileName)
    {
        var inspection = await InspectAsync(BrandSourceSampleFiles.Executable(), fileName);

        Assert.Equal(BrandSourceInspectionOutcome.Unsupported, inspection.Outcome);
    }

    [Fact]
    public async Task Text_under_an_unknown_extension_is_unsupported()
    {
        Assert.Equal(BrandSourceInspectionOutcome.Unsupported, (await InspectAsync(BrandSourceSampleFiles.Text(), "voice.rtf")).Outcome);
        Assert.Equal(BrandSourceInspectionOutcome.Unsupported, (await InspectAsync(BrandSourceSampleFiles.Text(), "voice")).Outcome);
    }

    [Fact]
    public async Task Text_that_is_not_utf8_or_carries_control_bytes_is_unsupported()
    {
        // UTF-16 with a byte-order mark, a NUL in otherwise plain text, and a lone continuation byte.
        byte[][] samples =
        [
            [0xFF, 0xFE, 0x68, 0x00, 0x69, 0x00],
            [0x68, 0x69, 0x00, 0x68, 0x69],
            [0x68, 0x69, 0x80, 0x68, 0x69],
        ];

        foreach (var sample in samples)
        {
            Assert.Equal(BrandSourceInspectionOutcome.Unsupported, (await InspectAsync(sample, "voice.txt")).Outcome);
        }
    }

    [Fact]
    public async Task Utf8_text_with_a_byte_order_mark_and_non_ascii_characters_is_accepted()
    {
        var inspection = await InspectAsync([0xEF, 0xBB, 0xBF, .. BrandSourceSampleFiles.Text("Crème brûlée — naïve café\r\n")], "voice.txt");

        Assert.Equal(BrandSourceInspectionOutcome.Accepted, inspection.Outcome);
    }

    [Fact]
    public async Task A_zip_that_is_not_a_word_package_is_unsupported()
    {
        Assert.Equal(BrandSourceInspectionOutcome.Unsupported, (await InspectAsync(BrandSourceSampleFiles.PlainZip(), "guide.docx")).Outcome);
    }

    [Fact]
    public async Task A_word_package_carrying_a_macro_project_is_unsupported()
    {
        Assert.Equal(
            BrandSourceInspectionOutcome.Unsupported,
            (await InspectAsync(BrandSourceSampleFiles.Docx(withMacros: true), "guide.docx")).Outcome);
    }

    [Fact]
    public async Task A_truncated_word_package_is_corrupt()
    {
        var docx = BrandSourceSampleFiles.Docx();

        Assert.Equal(BrandSourceInspectionOutcome.Corrupt, (await InspectAsync(docx[..(docx.Length / 2)], "guide.docx")).Outcome);
    }

    [Fact]
    public async Task A_pdf_without_its_trailer_is_corrupt()
    {
        Assert.Equal(BrandSourceInspectionOutcome.Corrupt, (await InspectAsync(BrandSourceSampleFiles.PdfWithoutTrailer(), "guide.pdf")).Outcome);
    }

    [Fact]
    public async Task An_image_whose_header_states_no_size_is_corrupt()
    {
        var png = BrandSourceSampleFiles.Png();
        var jpeg = BrandSourceSampleFiles.Jpeg();

        Assert.Equal(BrandSourceInspectionOutcome.Corrupt, (await InspectAsync(BrandSourceSampleFiles.Png(width: 0), "logo.png")).Outcome);
        Assert.Equal(BrandSourceInspectionOutcome.Corrupt, (await InspectAsync(png[..20], "logo.png")).Outcome);

        // Cut before the frame header: a JPEG that never says how big it is.
        Assert.Equal(BrandSourceInspectionOutcome.Corrupt, (await InspectAsync(jpeg[..22], "photo.jpg")).Outcome);

        // And a GIF, whose size lives in the descriptor after the six-byte header: a file cut inside it
        // states no canvas, and a canvas of zero is as corrupt here as it is for every other format.
        Assert.Equal(
            BrandSourceInspectionOutcome.Corrupt,
            (await InspectAsync(BrandSourceSampleFiles.Gif(width: 0), "loaf.gif")).Outcome);
        Assert.Equal(
            BrandSourceInspectionOutcome.Corrupt,
            (await InspectAsync(BrandSourceSampleFiles.Gif()[..9], "loaf.gif")).Outcome);
    }

    [Fact]
    public async Task An_image_declaring_more_pixels_than_the_cap_is_refused_as_too_large()
    {
        Assert.Equal(BrandSourceInspectionOutcome.ImageTooLarge, (await InspectAsync(BrandSourceSampleFiles.Png(10_000, 10_000), "logo.png")).Outcome);
        Assert.Equal(BrandSourceInspectionOutcome.ImageTooLarge, (await InspectAsync(BrandSourceSampleFiles.Jpeg(60_000, 60_000), "photo.jpg")).Outcome);
        Assert.Equal(BrandSourceInspectionOutcome.ImageTooLarge, (await InspectAsync(BrandSourceSampleFiles.Webp(16_000, 16_000), "photo.webp")).Outcome);
        Assert.Equal(
            BrandSourceInspectionOutcome.ImageTooLarge,
            (await InspectAsync(BrandSourceSampleFiles.Gif(10_000, 10_000), "loaf.gif")).Outcome);
    }

    [Fact]
    public async Task Size_limits_apply_per_format()
    {
        var text = new byte[BrandPolicy.SourceTextUploadMaxBytes + 1];
        Array.Fill(text, (byte)'a');

        Assert.Equal(BrandSourceInspectionOutcome.TooLarge, (await InspectAsync(text, "voice.txt")).Outcome);
        Assert.Equal(BrandSourceInspectionOutcome.Accepted, (await InspectAsync(text[..^1], "voice.txt")).Outcome);

        // A PDF may be larger than text may, up to the overall cap.
        var overall = BrandSourceSampleFiles.Pdf(padding: (int)BrandPolicy.SourceUploadMaxBytes);
        Assert.Equal(BrandSourceInspectionOutcome.TooLarge, (await InspectAsync(overall, "guide.pdf")).Outcome);
        Assert.Equal(
            BrandSourceInspectionOutcome.Accepted,
            (await InspectAsync(BrandSourceSampleFiles.Pdf(padding: (int)BrandPolicy.SourceTextUploadMaxBytes), "guide.pdf")).Outcome);
    }

    [Fact]
    public async Task An_empty_file_is_reported_as_empty()
    {
        Assert.Equal(BrandSourceInspectionOutcome.Empty, (await InspectAsync([], "voice.txt")).Outcome);
    }

    [Theory]
    [InlineData("guide.pdf", "guide.pdf")]
    [InlineData(@"C:\Users\sam\Documents\guide.pdf", "guide.pdf")]
    [InlineData("../../etc/guide.pdf", "guide.pdf")]
    [InlineData("  gui\u0000de\r\n.pdf  ", "guide.pdf")]
    public void A_filename_is_reduced_to_a_displayable_last_segment(string raw, string cleaned)
    {
        Assert.Equal(cleaned, BrandSourceFileName.Clean(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("folder/")]
    public void A_filename_with_nothing_left_is_no_filename(string? raw)
    {
        Assert.Null(BrandSourceFileName.Clean(raw));
    }

    /// <summary>
    /// The guard the download name depends on: every media type an upload can be stored as has an extension
    /// to be downloaded under.
    /// </summary>
    /// <remarks>
    /// Two lists in one class, and nothing else would notice them drifting — a format accepted tomorrow would
    /// download as a name with no extension, which is not an error anything throws.
    /// </remarks>
    [Fact]
    public void Every_accepted_media_type_can_name_an_extension()
    {
        Assert.NotEmpty(BrandSourceFileInspector.AcceptedMediaTypes);

        Assert.All(BrandSourceFileInspector.AcceptedMediaTypes, mediaType => Assert.False(
            string.IsNullOrWhiteSpace(BrandSourceFileInspector.CanonicalExtension(mediaType)),
            $"{mediaType} has no extension for a download to use."));
    }
}
