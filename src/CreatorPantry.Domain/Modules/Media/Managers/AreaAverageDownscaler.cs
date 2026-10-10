namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// Makes a picture smaller by averaging: each new pixel is the mean of the area of the original it covers.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It only ever shrinks, and never reshapes.</strong> The result is fitted inside the box with the
/// source's proportions kept, and a source already inside the box comes back as it is. Enlarging a picture
/// invents detail, and cropping or stretching one to a box would change a creator's work to suit a layout.
/// </para>
/// <para>
/// <strong>Area average, not a sampled filter,</strong> because it is exact for the job a rendition does:
/// every source pixel contributes in proportion to how much of it lies under the target pixel, a partly
/// covered one in part, so nothing is skipped and nothing aliases. It averages the stored sRGB values rather
/// than linear light, which B-28 records as accepted.
/// </para>
/// <para>
/// <strong>Colour is averaged weighted by alpha.</strong> A fully transparent pixel has a colour nobody
/// sees; averaging it in unweighted would bleed that colour into the visible edge beside it.
/// </para>
/// </remarks>
public static class AreaAverageDownscaler
{
    /// <summary>
    /// The largest size with the source's proportions that fits the box, or the source's own size when it
    /// already fits.
    /// </summary>
    public static (int Width, int Height) Fit(int width, int height, int maxWidth, int maxHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeight);

        if (width <= maxWidth && height <= maxHeight)
        {
            return (width, height);
        }

        var scale = Math.Min(maxWidth / (double)width, maxHeight / (double)height);

        // A very long, thin picture would round its short side to nothing; one pixel is the least it can be.
        return (
            Math.Clamp((int)Math.Round(width * scale, MidpointRounding.AwayFromZero), 1, maxWidth),
            Math.Clamp((int)Math.Round(height * scale, MidpointRounding.AwayFromZero), 1, maxHeight));
    }

    public static PixelBuffer Downscale(
        PixelBuffer source, int maxWidth, int maxHeight, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var (width, height) = Fit(source.Width, source.Height, maxWidth, maxHeight);

        if (width == source.Width && height == source.Height)
        {
            return source;
        }

        const int Channels = PixelBuffer.BytesPerPixel;
        var columns = Coverage(source.Width, width);
        var rows = Coverage(source.Height, height);
        var pixels = source.Rgba;
        var result = new byte[width * height * Channels];

        // One source row squeezed to the target width, and the sum of those for the target row being built.
        // Red, green and blue are held multiplied by alpha; the fourth value is the alpha itself.
        double[] squeezed = new double[width * Channels], sum = new double[width * Channels];
        var squeezedRow = -1;
        var hasTransparency = false;

        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(sum);

            for (var covered = 0; covered < rows.Weights[y].Length; covered++)
            {
                var sourceRow = rows.Start[y] + covered;

                // A source row that straddles two target rows is the last of one and the first of the next.
                if (sourceRow != squeezedRow)
                {
                    Squeeze(pixels.Slice(sourceRow * source.Width * Channels, source.Width * Channels), columns, squeezed);
                    squeezedRow = sourceRow;
                }

                var weight = rows.Weights[y][covered];

                for (var i = 0; i < sum.Length; i++)
                {
                    sum[i] += squeezed[i] * weight;
                }
            }

            var target = result.AsSpan(y * width * Channels, width * Channels);

            for (var i = 0; i < sum.Length; i += Channels)
            {
                var alpha = sum[i + 3];

                // Nothing visible was under this pixel, so it has no colour to report.
                if (alpha > 0)
                {
                    target[i] = Round(sum[i] / alpha);
                    target[i + 1] = Round(sum[i + 1] / alpha);
                    target[i + 2] = Round(sum[i + 2] / alpha);
                    target[i + 3] = Round(alpha);
                }

                hasTransparency |= target[i + 3] != byte.MaxValue;
            }
        }

        return new PixelBuffer(width, height, result, hasTransparency);
    }

    private static void Squeeze(ReadOnlySpan<byte> row, (int[] Start, double[][] Weights) columns, double[] squeezed)
    {
        const int Channels = PixelBuffer.BytesPerPixel;

        for (var x = 0; x < columns.Start.Length; x++)
        {
            double red = 0, green = 0, blue = 0, alpha = 0;
            var weights = columns.Weights[x];
            var at = columns.Start[x] * Channels;

            for (var covered = 0; covered < weights.Length; covered++, at += Channels)
            {
                var weighted = weights[covered] * row[at + 3];
                red += row[at] * weighted;
                green += row[at + 1] * weighted;
                blue += row[at + 2] * weighted;
                alpha += weighted;
            }

            var into = x * Channels;
            squeezed[into] = red;
            squeezed[into + 1] = green;
            squeezed[into + 2] = blue;
            squeezed[into + 3] = alpha;
        }
    }

    /// <summary>
    /// For each target position along one axis: the first source position under it, and how much of each
    /// source position it covers, as fractions that sum to one.
    /// </summary>
    private static (int[] Start, double[][] Weights) Coverage(int source, int target)
    {
        var span = source / (double)target;
        var start = new int[target];
        var weights = new double[target][];

        for (var i = 0; i < target; i++)
        {
            double from = i * span, to = (i + 1) * span;
            var first = (int)Math.Floor(from);

            // The epsilon keeps a boundary that lands on a whole pixel, give or take rounding, from
            // claiming a sliver of the next one.
            var end = Math.Clamp((int)Math.Ceiling(to - 1e-9), first + 1, source);

            start[i] = first;
            weights[i] = new double[end - first];

            for (var position = first; position < end; position++)
            {
                weights[i][position - first] = (Math.Min(to, position + 1) - Math.Max(from, position)) / span;
            }
        }

        return (start, weights);
    }

    private static byte Round(double value) => (byte)Math.Clamp(Math.Floor(value + 0.5), 0, byte.MaxValue);
}
