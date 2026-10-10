namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// A decoded picture: 8-bit RGBA, straight (not premultiplied) alpha, rows top to bottom.
/// </summary>
/// <remarks>
/// The one shape pixels take between the codecs in this module, so a decoder, the downscaler and an encoder
/// agree without a conversion in between. It is working memory, never a stored or returned thing: nothing
/// here knows a workspace, an asset or a file.
/// </remarks>
public sealed class PixelBuffer
{
    public const int BytesPerPixel = 4;

    private readonly byte[] rgba;

    /// <param name="hasTransparency">Whether any pixel is less than fully opaque.</param>
    public PixelBuffer(int width, int height, byte[] rgba, bool hasTransparency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(rgba);

        if (rgba.LongLength != (long)width * height * BytesPerPixel)
        {
            throw new ArgumentException("The buffer does not hold exactly width × height pixels.", nameof(rgba));
        }

        Width = width;
        Height = height;
        HasTransparency = hasTransparency;
        this.rgba = rgba;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// Whether any pixel is less than fully opaque.
    /// </summary>
    /// <remarks>
    /// Established while the pixels were produced rather than by scanning for it later, because a JPEG has
    /// no alpha and the rendition policy has to know before it picks an encoding.
    /// </remarks>
    public bool HasTransparency { get; }

    public ReadOnlySpan<byte> Rgba => rgba;
}
