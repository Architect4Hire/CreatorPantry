using CreatorPantry.Domain.Managers.FileInspection;

namespace CreatorPantry.Domain.Modules.Media.Managers;

public enum GeneratedImageInspectionOutcome
{
    Accepted = 1,

    /// <summary>No bytes at all.</summary>
    Empty = 2,

    /// <summary>Not one of the formats this will stage, whatever the provider labelled it.</summary>
    Unsupported = 3,

    /// <summary>Starts like a supported format and does not hold together as one.</summary>
    Corrupt = 4,

    /// <summary>More pixels than <see cref="MediaPolicy.ImageMaxPixels"/> allows.</summary>
    TooLarge = 5,

    /// <summary>
    /// A whole image followed by something that is not one: a polyglot, or a phone's motion-photo video.
    /// </summary>
    TrailingData = 6,
}

/// <param name="MediaType">Established from the bytes. Present exactly when the outcome is accepted.</param>
/// <param name="HasLocationMetadata">Whether readable EXIF or XMP in the file carries a GPS position.</param>
public readonly record struct GeneratedImageInspection(
    GeneratedImageInspectionOutcome Outcome,
    string? MediaType = null,
    int Width = 0,
    int Height = 0,
    bool HasLocationMetadata = false);

/// <summary>
/// Decides what a provider actually returned by reading it: PNG, JPEG, WebP or GIF, and the real pixel
/// dimensions of each.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing the provider said is an input.</strong> Not its declared media type, not the size that
/// was asked for, not a filename. A response is a stranger's bytes arriving over the network, and media.md
/// asks for a signature check rather than a label for exactly this reason. The media type and the
/// dimensions stored on a <c>GeneratedImage</c> are the ones this reads out of the file.
/// </para>
/// <para>
/// <strong>Nothing is resized, padded or re-encoded.</strong> A provider that returned a different shape
/// from the one requested has that shape recorded as it is. Stretching an image to the aspect a creator
/// asked for would silently distort their work, and the restriction on this job forbids it — a mismatch is
/// something to show them, not something to hide by scaling.
/// </para>
/// <para>
/// "Holds together" is structural, not a render: the whole file walked from its signature to its format's own
/// end, with nothing after it (15.2). No pixel is decoded, which also means this cannot be made to spend
/// arbitrary time or memory on a hostile file.
/// </para>
/// </remarks>
public static class GeneratedImageInspector
{
    public const string PngMediaType = ImageFileInspector.PngMediaType;

    public const string JpegMediaType = ImageFileInspector.JpegMediaType;

    public const string WebpMediaType = ImageFileInspector.WebpMediaType;

    public const string GifMediaType = ImageFileInspector.GifMediaType;

    /// <summary>Every media type a staged image may be stored as.</summary>
    public static readonly IReadOnlySet<string> AcceptedMediaTypes =
        new HashSet<string>(StringComparer.Ordinal) { PngMediaType, JpegMediaType, WebpMediaType, GifMediaType };

    /// <summary>
    /// The extension a download of this media type should carry, without its dot, or null for a type this
    /// does not know.
    /// </summary>
    /// <remarks>
    /// Here rather than next to the download, because this class is what decides a staged image's media
    /// type from its bytes: a format this can read and that had no extension to offer would be a drift
    /// between two lists, and <c>GeneratedImageInspectorTests</c> asserts the two stay in step. One per
    /// type — a JPEG is always <c>.jpg</c>, because a download name is ours to choose and determinism is
    /// the point.
    /// </remarks>
    public static string? CanonicalExtension(string? mediaType) =>
        mediaType is not null && CanonicalExtensions.TryGetValue(mediaType, out var extension)
            ? extension
            : null;

    private static readonly Dictionary<string, string> CanonicalExtensions = new(StringComparer.Ordinal)
    {
        [PngMediaType] = "png",
        [JpegMediaType] = "jpg",
        [WebpMediaType] = "webp",
        [GifMediaType] = "gif",
    };

    /// <summary>Reads the format and dimensions out of <paramref name="bytes"/>, or says why it will not.</summary>
    /// <remarks>
    /// The reading itself is <see cref="ImageFileInspector"/>'s, shared with every other module that accepts a
    /// picture; this holds it to <see cref="MediaPolicy.ImageMaxPixels"/>.
    /// </remarks>
    public static GeneratedImageInspection Inspect(ReadOnlySpan<byte> bytes)
    {
        var inspection = ImageFileInspector.Inspect(bytes, MediaPolicy.ImageMaxPixels);

        return inspection.Outcome switch
        {
            ImageFileInspectionOutcome.Accepted => new GeneratedImageInspection(
                GeneratedImageInspectionOutcome.Accepted,
                inspection.MediaType,
                inspection.Width,
                inspection.Height,
                inspection.HasLocationMetadata),
            ImageFileInspectionOutcome.Empty => new GeneratedImageInspection(GeneratedImageInspectionOutcome.Empty),
            ImageFileInspectionOutcome.Corrupt => new GeneratedImageInspection(GeneratedImageInspectionOutcome.Corrupt),
            ImageFileInspectionOutcome.TooLarge => new GeneratedImageInspection(GeneratedImageInspectionOutcome.TooLarge),
            ImageFileInspectionOutcome.TrailingData =>
                new GeneratedImageInspection(GeneratedImageInspectionOutcome.TrailingData),
            _ => new GeneratedImageInspection(GeneratedImageInspectionOutcome.Unsupported),
        };
    }
}
