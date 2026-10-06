using System.Globalization;

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// The name a staged generated image is offered under, safe in a <c>Content-Disposition</c> header and on
/// any file system, and the same for the same image every time.
/// </summary>
/// <remarks>
/// <para>
/// <c>generated-{n}.{ext}</c>, where <c>n</c> is the variant counted from one as a creator sees it. ASCII
/// <c>a-z0-9</c>, one hyphen and one dot by construction, so there is nothing to quote or encode — the same
/// guarantee <c>BrandSourceDownloadFileName</c> gives, reached more simply because a generated image has no
/// creator-supplied title to fold down.
/// </para>
/// <para>
/// <strong>It carries no id, no workspace and no storage location.</strong> The variant index is the only
/// thing in it, and that is a number between one and four that the client asked for. The prompt is not in
/// it: a prompt is private creator content, and a response header is the last place it should appear.
/// </para>
/// <para>
/// <strong>The extension comes from the stored media type</strong>, which was established from the bytes
/// the provider returned rather than from anything it claimed — so the name describes what is actually
/// there (IMG-005).
/// </para>
/// </remarks>
public static class GeneratedImageDownloadFileName
{
    /// <param name="variantIndex">The image's variant index, from zero.</param>
    /// <param name="mediaType">The image's stored media type, established from its bytes.</param>
    /// <returns>
    /// <c>generated-{n}.{ext}</c>, or <c>generated-{n}</c> for a media type the inspector cannot name an
    /// extension for — unreachable for anything staging stored, and a missing extension rather than a wrong
    /// one if it ever is reached.
    /// </returns>
    public static string For(int variantIndex, string mediaType)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(variantIndex);

        var name = $"generated-{(variantIndex + 1).ToString(CultureInfo.InvariantCulture)}";

        return GeneratedImageInspector.CanonicalExtension(mediaType) is { } extension
            ? $"{name}.{extension}"
            : name;
    }
}
