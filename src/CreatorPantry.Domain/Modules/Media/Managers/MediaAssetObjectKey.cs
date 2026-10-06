using System.Globalization;
using System.Text.RegularExpressions;

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// The grammar of a DAM object's key, and the only code that writes or reads one.
/// </summary>
/// <remarks>
/// <para>
/// <c>workspaces/{workspace}/media-assets/{asset}/{version}</c>, where every identifier is a 32-character
/// lowercase hex GUID and the version is its number.
/// </para>
/// <para>
/// <strong>Its own container, not a prefix inside the staging one.</strong> A staged image is deleted a
/// fortnight after nobody chose it; a DAM version is kept until a creator deletes the asset. Two lifetimes
/// and two sweeps, so two containers — and a reconciliation that walks one can never reach the other.
/// </para>
/// <para>
/// <strong>Built from identifiers only.</strong> No filename, title or other client text is ever part of a
/// key, so there is nothing a caller could put a traversal sequence in. <see cref="TryParse"/> is the other
/// half: a key read back from a row is honoured only if it matches this grammar exactly.
/// </para>
/// <para>
/// The version segment is the version <em>number</em> rather than its id, because a version's number is
/// unique per asset by index and reads better in a storage listing. The pair is still unique, which is what
/// <c>UX_MediaAssetVersions_ObjectKey</c> needs.
/// </para>
/// </remarks>
public static partial class MediaAssetObjectKey
{
    /// <summary>The private container every DAM object lives in. Declared in the AppHost.</summary>
    public const string Container = "media-assets";

    public static string For(Guid workspaceId, Guid mediaAssetId, int versionNumber)
    {
        ThrowIfEmpty(workspaceId, nameof(workspaceId));
        ThrowIfEmpty(mediaAssetId, nameof(mediaAssetId));
        ArgumentOutOfRangeException.ThrowIfLessThan(versionNumber, 1);

        return $"workspaces/{workspaceId:N}/media-assets/{mediaAssetId:N}/"
            + versionNumber.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Every DAM object of one workspace sits under this. The only prefix a sweep may list.</summary>
    public static string PrefixFor(Guid workspaceId)
    {
        ThrowIfEmpty(workspaceId, nameof(workspaceId));

        return $"workspaces/{workspaceId:N}/media-assets/";
    }

    /// <summary>Reads a stored key back into its parts. False for anything this type would not have written.</summary>
    public static bool TryParse(string? objectKey, out MediaAssetObjectKeyParts parts)
    {
        parts = default;

        if (objectKey is null || objectKey.Length > MediaPolicy.ObjectKeyMaxLength)
        {
            return false;
        }

        var match = KeyPattern().Match(objectKey);

        if (!match.Success)
        {
            return false;
        }

        parts = new MediaAssetObjectKeyParts(
            Guid.ParseExact(match.Groups["workspace"].Value, "N"),
            Guid.ParseExact(match.Groups["asset"].Value, "N"),
            int.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture));

        return true;
    }

    private static void ThrowIfEmpty(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An object key needs a real identifier.", name);
        }
    }

    // \z rather than $, which would also accept a trailing newline. Nine digits keeps the version inside an
    // int, and a leading zero is not a different version, so "01" is not a key this would have written.
    [GeneratedRegex(
        @"\Aworkspaces/(?<workspace>[0-9a-f]{32})/media-assets/(?<asset>[0-9a-f]{32})/(?<version>[1-9][0-9]{0,8})\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

public readonly record struct MediaAssetObjectKeyParts(Guid WorkspaceId, Guid MediaAssetId, int VersionNumber);
