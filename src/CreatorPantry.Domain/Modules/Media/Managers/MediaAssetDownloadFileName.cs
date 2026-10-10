using System.Globalization;
using CreatorPantry.Domain.Managers.Text;

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// Names a downloaded DAM asset (DAM-007): <c>{title-slug}-v{n}.{ext}</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deterministic and safe by construction.</strong> The same asset, version and media type always produce
/// the same name, and <see cref="FileNameSlug"/> reduces any title at all to lower-case ASCII letters, digits and
/// single hyphens — so there is nothing to quote, escape or encode in the header, and nothing a title could carry
/// that becomes a path, a directory traversal or a second header.
/// </para>
/// <para>
/// <strong>The extension comes from the stored media type, never from the uploaded filename.</strong> That name is
/// the creator's own text, is optional, and may claim <c>.jpg</c> over bytes that are a PNG — which is exactly the
/// wrong extension this prompt rules out. A media type the inspector cannot name gets <em>no</em> extension rather
/// than a guessed one: unreachable for anything a version write stored, and a missing extension is recoverable
/// where a wrong one misleads whatever opens the file.
/// </para>
/// <para>
/// <strong>The version suffix is what stops two downloads colliding.</strong> Saving version 1 and version 2 of one
/// asset without it gives two identical names, and the second silently becomes "… (1)" with nothing recording which
/// is which. The same shape <c>BrandSourceDownloadFileName</c> uses, so the two downloads in this codebase are
/// named alike.
/// </para>
/// </remarks>
public static class MediaAssetDownloadFileName
{
    /// <summary>The word a title that folds away entirely falls back to.</summary>
    /// <remarks>
    /// A title of nothing but punctuation or non-Latin script slugs to the empty string, and a file called
    /// <c>-v2.jpg</c> is worse than one called <c>image-v2.jpg</c>. "image" rather than "asset" because it is what
    /// the creator is actually saving.
    /// </remarks>
    public const string Fallback = "image";

    /// <param name="title">The asset's title; any text at all, including null.</param>
    /// <param name="versionNumber">The version being downloaded, appended as <c>-v{n}</c>.</param>
    /// <param name="mediaType">The version's stored media type, established from its bytes.</param>
    public static string For(
        string? title, int versionNumber, string? mediaType, MediaRenditionPurpose? rendition = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(versionNumber, 1);

        var name = $"{FileNameSlug.From(title, Fallback)}-v{versionNumber.ToString(CultureInfo.InvariantCulture)}";

        // A rendition says which it is, so the web-size copy of a version and the version itself can sit in
        // one folder without the second becoming "… (1)" — the reason the name carries a version at all.
        if (rendition is not null)
        {
            name += "-" + MediaRenditionSelector.NameOf(rendition);
        }

        return GeneratedImageInspector.CanonicalExtension(mediaType) is { } extension
            ? $"{name}.{extension}"
            : name;
    }
}
