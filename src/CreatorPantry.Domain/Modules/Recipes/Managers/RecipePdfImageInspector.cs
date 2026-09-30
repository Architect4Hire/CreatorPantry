namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Decides whether image bytes may be embedded: a PNG or JPEG by file signature (never by a name or a
/// declared type), within the size and dimension limits. It reads the header only.
/// </summary>
internal static class RecipePdfImageInspector
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxDimension = 8000;

    public static bool IsEmbeddable(byte[]? bytes) =>
        bytes is { Length: > 0 and <= MaxBytes }
        && TryReadDimensions(bytes, out var width, out var height)
        && width is > 0 and <= MaxDimension
        && height is > 0 and <= MaxDimension;

    private static bool TryReadDimensions(byte[] b, out int width, out int height)
    {
        (width, height) = (0, 0);

        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (b.AsSpan().StartsWith(png))
        {
            // IHDR must be first: length(4) "IHDR"(4) width(4) height(4).
            if (b.Length < 24 || b[12] != 'I' || b[13] != 'H' || b[14] != 'D' || b[15] != 'R') return false;
            width = ReadInt(b, 16);
            height = ReadInt(b, 20);
            return true;
        }

        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
        {
            var i = 2;
            while (i + 3 < b.Length)
            {
                if (b[i] != 0xFF) return false;
                var marker = b[i + 1];
                if (marker == 0xFF) { i++; continue; }

                var isFrame = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
                if (isFrame)
                {
                    if (i + 8 >= b.Length) return false;
                    height = (b[i + 5] << 8) | b[i + 6];
                    width = (b[i + 7] << 8) | b[i + 8];
                    return true;
                }

                if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }
                i += 2 + ((b[i + 2] << 8) | b[i + 3]);
            }
        }

        return false;
    }

    private static int ReadInt(byte[] b, int offset) =>
        (b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3];
}
