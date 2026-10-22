using System.Buffers.Binary;

namespace CreatorPantry.Domain.Managers.FileInspection;

/// <summary>
/// Whether a file is also a ZIP: whether an archive reader that starts, as they all do, from the end would find a
/// directory there.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists beside the structural walks.</strong> A ZIP's end-of-central-directory record carries a
/// comment of up to 65,535 bytes, and a reader accepts it when the comment runs exactly to the end of the file. So a
/// crafted file can be a valid PNG, JPEG, GIF, WebP or PDF that ends where its own format says it ends <em>and</em> an
/// archive, with the image's or document's closing bytes sitting inside the ZIP comment. The format walk cannot see
/// that; this can.
/// </para>
/// <para>
/// <strong>Exact, not a byte search.</strong> A match is the record's signature at a place where its own declared
/// comment length lands on the final byte. Compressed image data spells the four signature bytes by chance once in
/// billions of positions; it also landing its length on the end of the file is not a chance worth refusing a
/// creator's photograph over.
/// </para>
/// </remarks>
public static class ArchiveTrailer
{
    /// <summary>The fixed part of an end-of-central-directory record, before its comment.</summary>
    public const int ZipEndRecordLength = 22;

    /// <summary>The furthest from the end a reader will look: the fixed record plus the longest comment.</summary>
    public const int ZipSearchWindow = ZipEndRecordLength + ushort.MaxValue;

    private static ReadOnlySpan<byte> ZipEndSignature => [0x50, 0x4B, 0x05, 0x06];

    /// <summary>Whether a ZIP end-of-central-directory record is reachable from the last byte of <paramref name="bytes"/>.</summary>
    public static bool EndsInZipDirectory(ReadOnlySpan<byte> bytes)
    {
        var windowStart = Math.Max(0, bytes.Length - ZipSearchWindow);
        var window = bytes[windowStart..];
        var searchEnd = window.Length;

        while (searchEnd > 0)
        {
            var found = window[..searchEnd].LastIndexOf(ZipEndSignature);

            if (found < 0)
            {
                return false;
            }

            if (found + ZipEndRecordLength <= window.Length
                && found + ZipEndRecordLength + BinaryPrimitives.ReadUInt16LittleEndian(window[(found + 20)..]) == window.Length)
            {
                return true;
            }

            searchEnd = found + ZipEndSignature.Length - 1;
        }

        return false;
    }
}
