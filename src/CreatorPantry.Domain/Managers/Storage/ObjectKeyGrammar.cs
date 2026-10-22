using System.Text.RegularExpressions;

namespace CreatorPantry.Domain.Managers.Storage;

/// <summary>
/// What every object key in this application is made of, shared by the module key types that build them and the
/// store that receives them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Keys are server-generated, never caller-supplied.</strong> Each module's key type (<c>MediaAssetObjectKey</c>,
/// <c>BrandSourceObjectKey</c>, ...) writes its own family from GUIDs in their 32-digit form and positive integers,
/// and parses only that family back. This class holds what those families share: the one length cap, the refusal of
/// an empty identifier, and the character grammar every family is a strict subset of.
/// </para>
/// <para>
/// <strong>The store checks it too.</strong> A gateway already refuses a key it would not have written; the store
/// refusing anything outside the shared grammar means a gateway that forgot to is still not a path into storage
/// (SEC-005). Lowercase letters, digits and single hyphens in segments separated by single slashes: no dot, no
/// backslash, no percent sign, no empty or leading segment, so nothing a provider could read as a traversal, a scheme
/// or an escape.
/// </para>
/// </remarks>
public static partial class ObjectKeyGrammar
{
    /// <summary>The longest key any family may produce, and the longest the store accepts.</summary>
    public const int MaxLength = 400;

    /// <summary>An identifier as a key segment: 32 lowercase hex digits. A key never names the empty identifier.</summary>
    public static string Segment(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An object key needs a real identifier.", parameterName);
        }

        return id.ToString("N");
    }

    /// <summary>Whether <paramref name="key"/> is short enough to be a key at all — the first thing a family's parse asks.</summary>
    public static bool IsWithinLength(string? key) => key is { Length: > 0 and <= MaxLength };

    /// <summary>Whether <paramref name="key"/> is inside the grammar every family shares.</summary>
    public static bool IsWellFormed(string? key) => IsWithinLength(key) && KeyPattern().IsMatch(key!);

    /// <summary>Whether <paramref name="prefix"/> is a run of whole segments, ending at a slash, for a listing.</summary>
    public static bool IsWellFormedPrefix(string? prefix) =>
        IsWithinLength(prefix) && PrefixPattern().IsMatch(prefix!);

    /// <summary>Whether <paramref name="container"/> is a container name this application could have declared.</summary>
    public static bool IsWellFormedContainer(string? container) =>
        container is not null && ContainerPattern().IsMatch(container);

    /// <summary>
    /// Throws when a key, prefix or container is outside the grammar. For the store: a key this application did not
    /// write reaching it is a defect upstream, not an outcome to report.
    /// </summary>
    public static void ThrowIfMalformed(string container, string key)
    {
        if (!IsWellFormedContainer(container))
        {
            throw new ArgumentException("That is not a container this application declares.", nameof(container));
        }

        if (!IsWellFormed(key))
        {
            throw new ArgumentException("That is not an object key this application writes.", nameof(key));
        }
    }

    // \z rather than $, which would also accept a trailing newline. At least two segments: every family nests
    // under a workspace.
    [GeneratedRegex(@"\A[a-z0-9]+(?:-[a-z0-9]+)*(?:/[a-z0-9]+(?:-[a-z0-9]+)*)+\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"\A(?:[a-z0-9]+(?:-[a-z0-9]+)*/)+\z", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();

    [GeneratedRegex(@"\A[a-z0-9](?:-?[a-z0-9]){2,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerPattern();
}
