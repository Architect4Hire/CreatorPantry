using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// The downscaler's arithmetic, against pixels worked out by hand.
/// </summary>
public sealed class AreaAverageDownscalerTests
{
    [Theory]
    [InlineData(1024, 768, 1600, 1600, 1024, 768)]
    [InlineData(1600, 1600, 1600, 1600, 1600, 1600)]
    [InlineData(4096, 3072, 1600, 1600, 1600, 1200)]
    [InlineData(3072, 4096, 480, 480, 360, 480)]
    [InlineData(1024, 768, 480, 480, 480, 360)]
    [InlineData(8, 2, 4, 4, 4, 1)]
    [InlineData(10_000, 10, 100, 100, 100, 1)]
    public void A_picture_is_fitted_inside_the_box_in_its_own_proportions_and_never_enlarged(
        int width, int height, int maxWidth, int maxHeight, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), AreaAverageDownscaler.Fit(width, height, maxWidth, maxHeight));
    }

    [Fact]
    public void A_picture_that_already_fits_comes_back_untouched()
    {
        var source = Opaque(2, 2, 10, 20, 30, 40);

        Assert.Same(source, AreaAverageDownscaler.Downscale(source, 2, 2, TestContext.Current.CancellationToken));
        Assert.Same(source, AreaAverageDownscaler.Downscale(source, 500, 500, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Halving_averages_each_block_of_four()
    {
        // Red only; green and blue follow at fixed offsets so a channel mix-up cannot pass.
        var source = Opaque(
            4, 4,
            0, 4, 100, 104,
            8, 12, 108, 112,
            200, 200, 50, 52,
            200, 200, 54, 56);

        var result = AreaAverageDownscaler.Downscale(source, 2, 2, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Width);
        Assert.Equal(2, result.Height);
        Assert.False(result.HasTransparency);
        Assert.Equal(Opaque(2, 2, 6, 106, 200, 53).Rgba.ToArray(), result.Rgba.ToArray());
    }

    [Fact]
    public void A_pixel_only_partly_under_the_target_counts_in_part()
    {
        // Three to two: each target pixel covers one source pixel whole and half of the middle one, so the
        // weights are two thirds and one third. (0·2 + 90·1) / 3 = 30 and (90·1 + 210·2) / 3 = 170.
        var result = AreaAverageDownscaler.Downscale(Opaque(3, 1, 0, 90, 210), 2, 1, TestContext.Current.CancellationToken);

        Assert.Equal(Opaque(2, 1, 30, 170).Rgba.ToArray(), result.Rgba.ToArray());
    }

    [Fact]
    public void The_same_weighting_applies_down_the_rows()
    {
        var result = AreaAverageDownscaler.Downscale(Opaque(1, 3, 0, 90, 210), 1, 2, TestContext.Current.CancellationToken);

        Assert.Equal(Opaque(1, 2, 30, 170).Rgba.ToArray(), result.Rgba.ToArray());
    }

    [Fact]
    public void A_flat_opaque_picture_stays_exactly_flat_and_opaque_at_an_awkward_ratio()
    {
        // Seven to three and five to two: weights that are not exact in binary. A sum that came out a hair
        // under one would darken the picture or leave it very slightly see-through.
        var source = Opaque(7, 5, Enumerable.Repeat(9, 35).ToArray());

        var result = AreaAverageDownscaler.Downscale(source, 3, 3, TestContext.Current.CancellationToken);

        Assert.Equal((3, 2), (result.Width, result.Height));
        Assert.False(result.HasTransparency);
        Assert.Equal(Opaque(3, 2, 9, 9, 9, 9, 9, 9).Rgba.ToArray(), result.Rgba.ToArray());
    }

    [Fact]
    public void A_transparent_pixel_lends_its_coverage_but_not_its_colour()
    {
        // Opaque red beside fully transparent green. Averaged naively this is a half-transparent olive.
        var source = new PixelBuffer(2, 1, [255, 0, 0, 255, 0, 255, 0, 0], hasTransparency: true);

        var result = AreaAverageDownscaler.Downscale(source, 1, 1, TestContext.Current.CancellationToken);

        Assert.Equal([255, 0, 0, 128], result.Rgba.ToArray());
        Assert.True(result.HasTransparency);
    }

    [Fact]
    public void Colour_is_weighted_by_how_opaque_each_pixel_is()
    {
        // Alpha 255 and 85 is three to one, so red is (200·3 + 40·1) / 4 = 160 and alpha is their mean.
        var source = new PixelBuffer(2, 1, [200, 0, 0, 255, 40, 0, 0, 85], hasTransparency: true);

        var result = AreaAverageDownscaler.Downscale(source, 1, 1, TestContext.Current.CancellationToken);

        Assert.Equal([160, 0, 0, 170], result.Rgba.ToArray());
    }

    [Fact]
    public void Nothing_visible_under_a_pixel_leaves_it_empty()
    {
        var source = new PixelBuffer(2, 2, [.. Enumerable.Repeat<byte[]>([70, 80, 90, 0], 4).SelectMany(p => p)], hasTransparency: true);

        var result = AreaAverageDownscaler.Downscale(source, 1, 1, TestContext.Current.CancellationToken);

        Assert.Equal([0, 0, 0, 0], result.Rgba.ToArray());
    }

    [Fact]
    public void Cancellation_stops_it()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => AreaAverageDownscaler.Downscale(Opaque(2, 1, 1, 2), 1, 1, cancelled.Token));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 10)]
    public void A_box_with_no_room_in_it_is_a_caller_error(int maxWidth, int maxHeight)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AreaAverageDownscaler.Downscale(Opaque(2, 1, 1, 2), maxWidth, maxHeight, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// An opaque picture from one number per pixel: red is the number, green is ten more, blue twenty more.
    /// </summary>
    private static PixelBuffer Opaque(int width, int height, params int[] reds) =>
        new(
            width,
            height,
            [.. reds.SelectMany(red => new[] { (byte)red, (byte)(red + 10), (byte)(red + 20), byte.MaxValue })],
            hasTransparency: false);
}
