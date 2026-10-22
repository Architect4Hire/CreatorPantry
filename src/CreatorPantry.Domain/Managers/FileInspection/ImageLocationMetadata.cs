using System.Buffers.Binary;

namespace CreatorPantry.Domain.Managers.FileInspection;

/// <summary>
/// Whether the metadata blocks an image carries hold a GPS position: EXIF's GPS directory, or an XMP packet naming
/// a latitude or longitude.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Detection, not removal.</strong> An original is kept byte for byte (media.md, DAM-010), so a location in it
/// is reported to the creator rather than stripped. Renditions are re-encoded with no metadata at all.
/// </para>
/// <para>
/// <strong>Never a reason to refuse a picture.</strong> Every read here is bounded and a malformed block is simply
/// "no location this could read": whether a camera wrote its EXIF correctly says nothing about whether the image is
/// one.
/// </para>
/// </remarks>
public static class ImageLocationMetadata
{
    private const ushort GpsDirectoryTag = 0x8825;

    private const int TiffEntryLength = 12;

    /// <summary>EXIF's own prefix, which JPEG always and WebP sometimes write before the TIFF structure.</summary>
    private static ReadOnlySpan<byte> ExifPrefix => "Exif\0\0"u8;

    private static ReadOnlySpan<byte> XmpNamespace => "http://ns.adobe.com/xap/1.0/\0"u8;

    private static ReadOnlySpan<byte> ExtendedXmpNamespace => "http://ns.adobe.com/xmp/extension/\0"u8;

    /// <summary>The iTXt keyword XMP is stored under, then the compression flag that must be zero to read it.</summary>
    private static ReadOnlySpan<byte> PngXmpKeyword => "XML:com.adobe.xmp\0"u8;

    /// <summary>A JPEG APP1 segment's payload: EXIF, or a main or extended XMP packet.</summary>
    public static bool InJpegApp1(ReadOnlySpan<byte> segment)
    {
        if (segment.StartsWith(ExifPrefix))
        {
            return InTiff(segment[ExifPrefix.Length..]);
        }

        return (segment.StartsWith(XmpNamespace) || segment.StartsWith(ExtendedXmpNamespace)) && InXmp(segment);
    }

    /// <summary>A PNG <c>eXIf</c> or WebP <c>EXIF</c> chunk: a TIFF structure, sometimes behind EXIF's prefix.</summary>
    public static bool InExif(ReadOnlySpan<byte> payload) =>
        InTiff(payload.StartsWith(ExifPrefix) ? payload[ExifPrefix.Length..] : payload);

    /// <summary>A PNG <c>iTXt</c> chunk, read only when it is uncompressed XMP.</summary>
    public static bool InPngText(ReadOnlySpan<byte> payload) =>
        payload.StartsWith(PngXmpKeyword)
        && payload.Length > PngXmpKeyword.Length
        && payload[PngXmpKeyword.Length] == 0
        && InXmp(payload);

    /// <summary>An XMP packet that names a GPS latitude or longitude property.</summary>
    public static bool InXmp(ReadOnlySpan<byte> packet) =>
        packet.IndexOf("GPSLatitude"u8) >= 0 || packet.IndexOf("GPSLongitude"u8) >= 0;

    /// <summary>
    /// A TIFF structure whose first directory points at a GPS directory that holds a latitude or longitude.
    /// </summary>
    private static bool InTiff(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return false;
        }

        bool little;

        if (tiff.StartsWith("II*\0"u8))
        {
            little = true;
        }
        else if (tiff.StartsWith("MM\0*"u8))
        {
            little = false;
        }
        else
        {
            return false;
        }

        var gps = DirectoryEntryValue(tiff, ReadUInt32(tiff[4..], little), GpsDirectoryTag, little);

        if (gps is not { } gpsOffset || !TryReadDirectory(tiff, gpsOffset, little, out var start, out var count))
        {
            return false;
        }

        for (var entry = 0; entry < count; entry++)
        {
            // GPSLatitudeRef, GPSLatitude, GPSLongitudeRef, GPSLongitude. A directory that carries only a version
            // or a timestamp has no position in it.
            if (ReadUInt16(tiff[(start + entry * TiffEntryLength)..], little) is >= 1 and <= 4)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The 32-bit value of <paramref name="tag"/> in the directory at <paramref name="offset"/>, or null.</summary>
    private static uint? DirectoryEntryValue(ReadOnlySpan<byte> tiff, uint offset, ushort tag, bool little)
    {
        if (!TryReadDirectory(tiff, offset, little, out var start, out var count))
        {
            return null;
        }

        for (var entry = 0; entry < count; entry++)
        {
            var at = tiff[(start + entry * TiffEntryLength)..];

            if (ReadUInt16(at, little) == tag)
            {
                return ReadUInt32(at[8..], little);
            }
        }

        return null;
    }

    private static bool TryReadDirectory(ReadOnlySpan<byte> tiff, uint offset, bool little, out int start, out int count)
    {
        (start, count) = (0, 0);

        if (offset > (uint)(tiff.Length - 2))
        {
            return false;
        }

        count = ReadUInt16(tiff[(int)offset..], little);
        start = (int)offset + 2;

        return (long)start + (long)count * TiffEntryLength <= tiff.Length;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
}
