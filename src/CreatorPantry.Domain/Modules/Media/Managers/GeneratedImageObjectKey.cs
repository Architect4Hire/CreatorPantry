using System.Globalization;
using System.Text.RegularExpressions;

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// The grammar of a staged generated image's object key, and the only code that writes or reads one.
/// </summary>
/// <remarks>
/// <para>
/// <c>workspaces/{workspace}/generated-images/{operation}/{variant}</c>, where the identifiers are
/// 32-character lowercase hex GUIDs and the variant is the index within the request.
/// </para>
/// <para>
/// <strong>Deterministic, which is what makes a replay safe.</strong> A worker that stored an object and
/// died before recording the row computes the same key on its next attempt, so it finds the orphan instead
/// of leaving a second one beside it. Nothing random, nothing timestamped, and nothing derived from the
/// prompt — a key built from creator text would both leak it to whoever can list the container and give a
/// caller somewhere to put a traversal sequence.
/// </para>
/// <para>
/// <see cref="TryParse"/> is the other half: a key read back from a row is honoured only if it matches this
/// grammar exactly, which a key with <c>..</c>, a backslash, an encoded separator, a scheme or a trailing
/// segment does not. The workspace is in the key so the gateway can refuse a neighbour's before touching
/// storage (tenancy.md).
/// </para>
/// </remarks>
public static partial class GeneratedImageObjectKey
{
    /// <summary>The private container every staged generated image lives in. Declared in the AppHost.</summary>
    public const string Container = "generated-images";

    /// <summary>Every staging object of one workspace sits under this. The only prefix a sweep may list.</summary>
    public static string PrefixFor(Guid workspaceId)
    {
        ThrowIfEmpty(workspaceId, nameof(workspaceId));

        return $"workspaces/{workspaceId:N}/generated-images/";
    }

    public static string For(Guid workspaceId, Guid operationId, int variantIndex)
    {
        ThrowIfEmpty(workspaceId, nameof(workspaceId));
        ThrowIfEmpty(operationId, nameof(operationId));
        ArgumentOutOfRangeException.ThrowIfNegative(variantIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(variantIndex, MediaPolicy.MaxVariantsPerOperation);

        return $"workspaces/{workspaceId:N}/generated-images/{operationId:N}/"
            + variantIndex.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Reads a stored key back into its parts. False for anything this type would not have written.</summary>
    public static bool TryParse(string? objectKey, out GeneratedImageObjectKeyParts parts)
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

        var variant = int.Parse(match.Groups["variant"].Value, CultureInfo.InvariantCulture);

        if (variant >= MediaPolicy.MaxVariantsPerOperation)
        {
            // The grammar allows one digit; the policy allows fewer values than that. Checked rather than
            // folded into the pattern so raising the cap is one edit in one place.
            return false;
        }

        parts = new GeneratedImageObjectKeyParts(
            Guid.ParseExact(match.Groups["workspace"].Value, "N"),
            Guid.ParseExact(match.Groups["operation"].Value, "N"),
            variant);

        return true;
    }

    private static void ThrowIfEmpty(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An object key needs a real identifier.", name);
        }
    }

    // \z rather than $, which would also accept a trailing newline. One digit, with the policy cap checked
    // above: a leading zero is not a different variant, so "01" is not a key this would have written.
    [GeneratedRegex(
        @"\Aworkspaces/(?<workspace>[0-9a-f]{32})/generated-images/(?<operation>[0-9a-f]{32})/(?<variant>[0-9])\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

public readonly record struct GeneratedImageObjectKeyParts(Guid WorkspaceId, Guid OperationId, int VariantIndex);
