using System.Globalization;
using CreatorPantry.Domain.Managers.Text;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// The name a downloaded brand source document is offered under: safe in a <c>Content-Disposition</c> header
/// and on any file system, and the same for the same version every time.
/// </summary>
/// <remarks>
/// <para>
/// Built from the document's <em>title</em> and the version number, never from
/// <c>BrandSourceDocumentVersion.OriginalFileName</c>. The creator's own filename is display text that
/// arrived with an upload: it is cleaned of control characters but is otherwise whatever a client sent, so
/// putting it in a response header would be putting untrusted text where a quote or a separator changes the
/// header's meaning. The title goes through <see cref="FileNameSlug"/> and comes out <c>a-z0-9</c> and
/// hyphens, which has nothing left to escape.
/// </para>
/// <para>
/// <strong>The extension comes from the stored media type</strong>, which was established from the file's
/// bytes: the extension describes what is there rather than repeating a claim. A wholesale mismatch cannot
/// arise — the upload refuses a file whose bytes are not the format its extension claims — but a format
/// accepted under more than one spelling can, and a file arriving as <c>hero.jpeg</c> downloads as
/// <c>.jpg</c>, because one version should have one name.
/// </para>
/// <para>
/// It carries no workspace, no id and no storage location, so the name discloses nothing a client did not
/// already send.
/// </para>
/// </remarks>
public static class BrandSourceDownloadFileName
{
    /// <summary>The word a title that folds away entirely falls back to.</summary>
    public const string Fallback = "document";

    /// <param name="title">The document's title; any text at all.</param>
    /// <param name="versionNumber">The version being downloaded, appended as <c>-v{n}</c>.</param>
    /// <param name="mediaType">The version's stored media type, established from its bytes.</param>
    /// <returns>
    /// <c>slug-v{n}.ext</c>, or <c>slug-v{n}</c> for a media type the inspector cannot name an extension for —
    /// unreachable for anything an upload stored, and a missing extension rather than a wrong one if it ever
    /// is reached.
    /// </returns>
    public static string For(string? title, int versionNumber, string mediaType)
    {
        var name = $"{FileNameSlug.From(title, Fallback)}-v{versionNumber.ToString(CultureInfo.InvariantCulture)}";

        return BrandSourceFileInspector.CanonicalExtension(mediaType) is { } extension
            ? $"{name}.{extension}"
            : name;
    }
}
