namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// The name of a rendition's object: its source's own key with the purpose after it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Beside the source, in the source's container.</strong> A rendition of a staged image lives in
/// staging storage and one of a DAM version in the DAM's, each under the same workspace prefix as the
/// picture it was made from. So a rendition is authorized by exactly the check its source is — the
/// workspace in the key — and no third container needs a policy of its own.
/// </para>
/// <para>
/// <strong>Never a source key.</strong> The suffix keeps the two grammars apart: nothing that parses here
/// parses as a staged image or a DAM version, so the staging reconciliation, which only ever considers
/// keys it can parse as a staged image, does not see a rendition at all — and cannot mistake one for an
/// orphan.
/// </para>
/// <para>
/// Like every other key in this module it is built only from identifiers, never from creator text, is
/// never a URL, and is never returned to a client (media.md).
/// </para>
/// </remarks>
public static class MediaRenditionObjectKey
{
    private const char Separator = '-';

    public static string For(string sourceObjectKey, MediaRenditionPurpose purpose) =>
        TryFor(sourceObjectKey, purpose, out var parts)
            ? parts.ObjectKey
            : throw new ArgumentException(
                "A rendition key needs the key of a staged image or a DAM version.", nameof(sourceObjectKey));

    /// <summary>
    /// The rendition key for a source, with what it implies. False when the source key is not one this
    /// module writes.
    /// </summary>
    public static bool TryFor(string? sourceObjectKey, MediaRenditionPurpose purpose, out MediaRenditionObjectKeyParts parts)
    {
        parts = default;

        if (!TryContainer(sourceObjectKey, out var container, out var workspaceId))
        {
            return false;
        }

        parts = new MediaRenditionObjectKeyParts(
            container, workspaceId, sourceObjectKey!, purpose, sourceObjectKey + Separator + Suffix(purpose));

        return true;
    }

    /// <summary>Reads a stored key back into its parts. False for anything this type would not have written.</summary>
    public static bool TryParse(string? objectKey, out MediaRenditionObjectKeyParts parts)
    {
        parts = default;

        if (objectKey is null || objectKey.Length > MediaPolicy.ObjectKeyMaxLength)
        {
            return false;
        }

        var cut = objectKey.LastIndexOf(Separator);

        if (cut < 0)
        {
            return false;
        }

        var purpose = objectKey[(cut + 1)..] switch
        {
            "web" => MediaRenditionPurpose.Web,
            "thumbnail" => MediaRenditionPurpose.Thumbnail,
            _ => MediaRenditionPurpose.Unspecified,
        };

        var source = objectKey[..cut];

        if (purpose is MediaRenditionPurpose.Unspecified || !TryContainer(source, out var container, out var workspaceId))
        {
            return false;
        }

        parts = new MediaRenditionObjectKeyParts(container, workspaceId, source, purpose, objectKey);

        return true;
    }

    private static bool TryContainer(string? sourceObjectKey, out string container, out Guid workspaceId)
    {
        if (GeneratedImageObjectKey.TryParse(sourceObjectKey, out var staged))
        {
            container = GeneratedImageObjectKey.Container;
            workspaceId = staged.WorkspaceId;

            return true;
        }

        if (MediaAssetObjectKey.TryParse(sourceObjectKey, out var version))
        {
            container = MediaAssetObjectKey.Container;
            workspaceId = version.WorkspaceId;

            return true;
        }

        container = string.Empty;
        workspaceId = Guid.Empty;

        return false;
    }

    private static string Suffix(MediaRenditionPurpose purpose) => purpose switch
    {
        MediaRenditionPurpose.Web => "web",
        MediaRenditionPurpose.Thumbnail => "thumbnail",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };
}

/// <param name="Container">The container the source lives in, which is where the rendition lives too.</param>
public readonly record struct MediaRenditionObjectKeyParts(
    string Container, Guid WorkspaceId, string SourceObjectKey, MediaRenditionPurpose Purpose, string ObjectKey);
