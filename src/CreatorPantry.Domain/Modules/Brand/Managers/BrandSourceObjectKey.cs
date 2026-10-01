using System.Globalization;
using System.Text.RegularExpressions;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// The grammar of a brand source object's key, and the only code that writes or reads one.
/// </summary>
/// <remarks>
/// <para>
/// <c>workspaces/{workspace}/brand-sources/{document}/{version}/original</c>, or
/// <c>…/{version}/extracted/{ordinal}</c> for extracted text. Every identifier is a 32-character lowercase hex
/// GUID and the ordinal a positive integer.
/// </para>
/// <para>
/// <strong>Built from identifiers only.</strong> No filename, title, extension or other client text is ever
/// part of a key, so there is nothing a caller could put a traversal sequence in. <see cref="TryParse"/> is
/// the other half: a key read back from a row is used only if it matches this grammar exactly, which a key
/// with <c>..</c>, a backslash, an encoded separator, a scheme or a trailing segment does not.
/// </para>
/// <para>
/// The version segment is the version's id rather than its number, so the key is settled before the row that
/// will record it exists.
/// </para>
/// </remarks>
public static partial class BrandSourceObjectKey
{
    /// <summary>The private container every brand source object lives in. Declared in the AppHost.</summary>
    public const string Container = "brand-sources";

    public static string ForOriginal(Guid workspaceId, Guid documentId, Guid versionId) =>
        $"{Prefix(workspaceId, documentId, versionId)}/original";

    public static string ForExtractedText(Guid workspaceId, Guid documentId, Guid versionId, int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1);

        return $"{Prefix(workspaceId, documentId, versionId)}/extracted/{ordinal.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Reads a stored key back into its parts. False for anything this type would not have written.</summary>
    public static bool TryParse(string? objectKey, out BrandSourceObjectKeyParts parts)
    {
        parts = default;

        if (objectKey is null || objectKey.Length > BrandPolicy.ObjectKeyMaxLength)
        {
            return false;
        }

        var match = KeyPattern().Match(objectKey);

        if (!match.Success)
        {
            return false;
        }

        parts = new BrandSourceObjectKeyParts(
            Guid.ParseExact(match.Groups["workspace"].Value, "N"),
            Guid.ParseExact(match.Groups["document"].Value, "N"),
            Guid.ParseExact(match.Groups["version"].Value, "N"),
            match.Groups["ordinal"].Success ? int.Parse(match.Groups["ordinal"].Value, CultureInfo.InvariantCulture) : null);

        return true;
    }

    private static string Prefix(Guid workspaceId, Guid documentId, Guid versionId)
    {
        ThrowIfEmpty(workspaceId, nameof(workspaceId));
        ThrowIfEmpty(documentId, nameof(documentId));
        ThrowIfEmpty(versionId, nameof(versionId));

        return $"workspaces/{workspaceId:N}/brand-sources/{documentId:N}/{versionId:N}";
    }

    private static void ThrowIfEmpty(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An object key needs a real identifier.", name);
        }
    }

    // \z rather than $, which would also accept a trailing newline. Nine ordinal digits keeps it inside an int.
    [GeneratedRegex(
        @"\Aworkspaces/(?<workspace>[0-9a-f]{32})/brand-sources/(?<document>[0-9a-f]{32})/(?<version>[0-9a-f]{32})/(?:original|extracted/(?<ordinal>[1-9][0-9]{0,8}))\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

/// <param name="ExtractionOrdinal">Null for the original upload; the extraction's ordinal for extracted text.</param>
public readonly record struct BrandSourceObjectKeyParts(Guid WorkspaceId, Guid DocumentId, Guid VersionId, int? ExtractionOrdinal);
