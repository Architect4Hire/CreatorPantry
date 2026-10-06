using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Tests.Brand;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// What the job will and will not stage, decided by reading the bytes.
/// </summary>
/// <remarks>
/// The same sample builders <c>BrandSourceFileInspectorTests</c> uses, so "a real PNG" means one thing
/// across both suites — and a sample that stops being valid fails both rather than quietly passing here.
/// </remarks>
public sealed class GeneratedImageInspectorTests
{
    public static TheoryData<string, byte[], string, int, int> RealImages() => new()
    {
        { "png", BrandSourceSampleFiles.Png(width: 640, height: 480), "image/png", 640, 480 },
        { "jpeg", BrandSourceSampleFiles.Jpeg(width: 1024, height: 768), "image/jpeg", 1024, 768 },
        { "webp", BrandSourceSampleFiles.Webp(width: 512, height: 512), "image/webp", 512, 512 },
        { "gif", BrandSourceSampleFiles.Gif(width: 300, height: 200), "image/gif", 300, 200 },
    };

    [Theory]
    [MemberData(nameof(RealImages))]
    public void A_supported_image_reports_its_own_type_and_its_own_dimensions(
        string format, byte[] bytes, string mediaType, int width, int height)
    {
        var inspection = GeneratedImageInspector.Inspect(bytes);

        Assert.Equal(GeneratedImageInspectionOutcome.Accepted, inspection.Outcome);
        Assert.Equal(mediaType, inspection.MediaType);

        // Not the size that was asked for: the size the file says it is. Nothing is stretched to fit.
        Assert.Equal(width, inspection.Width);
        Assert.Equal(height, inspection.Height);
        Assert.Contains(format, mediaType, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_accepted_media_type_is_one_the_inspector_can_produce()
    {
        var produced = RealImages()
            .Select(row => row.Data.Item3)
            .ToHashSet(StringComparer.Ordinal);

        // A format added to the accepted set without a case that proves it is readable would otherwise let
        // the job stage something nothing can name the dimensions of.
        Assert.Equal(GeneratedImageInspector.AcceptedMediaTypes.Order(), produced.Order());
    }

    [Fact]
    public void Nothing_is_an_image_because_it_was_labelled_one()
    {
        // A PE executable, which a provider could perfectly well have labelled image/png.
        Assert.Equal(
            GeneratedImageInspectionOutcome.Unsupported,
            GeneratedImageInspector.Inspect(BrandSourceSampleFiles.Executable()).Outcome);
    }

    [Fact]
    public void Empty_bytes_are_empty_rather_than_unsupported()
    {
        Assert.Equal(GeneratedImageInspectionOutcome.Empty, GeneratedImageInspector.Inspect([]).Outcome);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(20)]
    public void A_truncated_png_holds_together_as_nothing(int length)
    {
        Assert.Equal(
            GeneratedImageInspectionOutcome.Corrupt,
            GeneratedImageInspector.Inspect(BrandSourceSampleFiles.Png()[..length]).Outcome);
    }

    [Fact]
    public void A_jpeg_with_no_frame_header_is_corrupt_rather_than_a_zero_sized_image()
    {
        Assert.Equal(
            GeneratedImageInspectionOutcome.Corrupt,
            GeneratedImageInspector.Inspect([0xFF, 0xD8, 0xFF, 0xD9]).Outcome);
    }

    [Fact]
    public void An_image_past_the_pixel_cap_is_refused_before_anything_stores_it()
    {
        // Within what a PNG header can state and well past what this workspace will stage.
        var inspection = GeneratedImageInspector.Inspect(
            BrandSourceSampleFiles.Png(width: 20_000, height: 20_000));

        Assert.Equal(GeneratedImageInspectionOutcome.TooLarge, inspection.Outcome);
        Assert.True(20_000L * 20_000 > MediaPolicy.ImageMaxPixels);
    }

    [Fact]
    public void An_image_claiming_a_zero_dimension_is_corrupt()
    {
        Assert.Equal(
            GeneratedImageInspectionOutcome.Corrupt,
            GeneratedImageInspector.Inspect(BrandSourceSampleFiles.Png(width: 0, height: 4)).Outcome);
    }

    [Fact]
    public void An_animated_gif_reports_the_canvas_every_frame_shares()
    {
        var inspection = GeneratedImageInspector.Inspect(
            BrandSourceSampleFiles.Gif(width: 48, height: 32, frames: 3));

        Assert.Equal(GeneratedImageInspectionOutcome.Accepted, inspection.Outcome);
        Assert.Equal(48, inspection.Width);
        Assert.Equal(32, inspection.Height);
    }
}
