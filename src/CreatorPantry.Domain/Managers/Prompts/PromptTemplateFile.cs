using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreatorPantry.Domain.Managers.Prompts;

/// <summary>
/// Reads one <c>.prompt.md</c> file: a JSON front-matter manifest fenced by <c>---</c>, then the body.
/// </summary>
/// <remarks>
/// <para>
/// JSON front matter rather than YAML because the solution has no YAML package and this does not justify
/// adding one; <c>.md</c> rather than <c>.txt</c> because <c>.gitattributes</c> already declares
/// <c>*.md text eol=lf</c>, which is what makes a body checksum the same value in a Windows checkout and a
/// Linux one rather than a coin flip. Manifest and body share a file so they cannot drift apart and one
/// checksum covers the artifact a reviewer actually reads.
/// </para>
/// <para>
/// Public so that a malformed case can be expressed as a few lines of inline text in a test instead of an
/// embedded fixture per case.
/// </para>
/// </remarks>
public static class PromptTemplateFile
{
    /// <summary>The suffix that marks a resource as a prompt template.</summary>
    public const string Extension = ".prompt.md";

    internal const string PlaceholderOpen = "{{";
    internal const string PlaceholderClose = "}}";

    private const string Fence = "---";
    private const string ChecksumPrefix = "sha256:";

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },

        // A mistyped property is the failure this catches: "safteyClass" would otherwise be ignored, leaving
        // safetyClass Unspecified, and the message would be about a missing declaration rather than the typo
        // in front of the reader. A misspelled "inputs" would empty the contract instead.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <param name="resourceName">
    /// The embedded resource name, which must end in <c>{id}-{version}.prompt.md</c>. Checked against the
    /// manifest so a file copied for a new version, with its manifest left behind, cannot load quietly under
    /// the wrong identity.
    /// </param>
    /// <exception cref="PromptTemplateException">The file is malformed, mis-declared, or checksum-mismatched.</exception>
    public static PromptTemplate Parse(string resourceName, string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);
        ArgumentNullException.ThrowIfNull(content);

        var (manifestJson, body) = SplitFrontMatter(resourceName, content);
        var manifest = Deserialize(resourceName, manifestJson);

        var id = RequireId(resourceName, manifest.Id);
        var version = RequireVersion(resourceName, manifest.Version);
        var outputSchemaVersion = RequireOutputSchemaVersion(resourceName, manifest.OutputSchemaVersion);
        var safetyClass = RequireSafetyClass(resourceName, manifest.SafetyClass);
        var inputs = RequireInputs(resourceName, manifest.Inputs);

        RequireFilenameMatchesManifest(resourceName, id, version);
        RequirePlaceholdersAndInputsAgree(resourceName, body, inputs);
        var checksum = RequireChecksum(resourceName, manifest.BodyChecksum, body);

        return new PromptTemplate(id, version, outputSchemaVersion, safetyClass, inputs, body, checksum);
    }

    /// <summary><c>sha256:</c> and lowercase hex over the normalized body.</summary>
    public static string ComputeChecksum(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        return ChecksumPrefix
            + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeBody(body))));
    }

    /// <summary>
    /// LF line endings and no trailing whitespace. Everything else — including leading blank lines and
    /// indentation — is significant, because in a prompt it is.
    /// </summary>
    /// <remarks>
    /// <c>.gitattributes</c> already normalizes <c>*.md</c> to LF in the working tree, so this is belt and
    /// braces for a resource that arrived some other way. It is applied before hashing and to the body that is
    /// kept, so the two can never disagree.
    /// </remarks>
    private static string NormalizeBody(string body) =>
        body.ReplaceLineEndings("\n").TrimEnd();

    private static (string Manifest, string Body) SplitFrontMatter(string resourceName, string content)
    {
        var lines = content.ReplaceLineEndings("\n").Split('\n');

        if (lines.Length == 0 || lines[0].Trim() != Fence)
        {
            throw new PromptTemplateException(
                resourceName, $"the file must open with a '{Fence}' front-matter fence.");
        }

        var closing = Array.FindIndex(lines, 1, line => line.Trim() == Fence);

        if (closing < 0)
        {
            throw new PromptTemplateException(
                resourceName, $"the front matter is never closed by a second '{Fence}' line.");
        }

        var manifest = string.Join('\n', lines[1..closing]);
        var body = NormalizeBody(string.Join('\n', lines[(closing + 1)..]));

        if (body.Length == 0)
        {
            throw new PromptTemplateException(resourceName, "the body is empty.");
        }

        return (manifest, body);
    }

    private static Manifest Deserialize(string resourceName, string manifestJson)
    {
        try
        {
            return JsonSerializer.Deserialize<Manifest>(manifestJson, ManifestJson)
                ?? throw new PromptTemplateException(resourceName, "the front matter is null.");
        }
        catch (JsonException exception)
        {
            throw new PromptTemplateException(
                resourceName, $"the front matter is not valid manifest JSON. {exception.Message}");
        }
    }

    private static string RequireId(string resourceName, string? id)
    {
        if (string.IsNullOrEmpty(id) || !IsDottedLowercase(id))
        {
            throw new PromptTemplateException(
                resourceName,
                "'id' must be lowercase dot-separated segments of letters, digits and hyphens, e.g. "
                + $"'recipe.concepts'; found '{id}'.");
        }

        return id;
    }

    private static PromptTemplateVersion RequireVersion(string resourceName, string? version) =>
        PromptTemplateVersion.TryParse(version, out var parsed)
            ? parsed
            : throw new PromptTemplateException(
                resourceName, $"'version' must be major.minor.patch; found '{version}'.");

    private static string RequireOutputSchemaVersion(string resourceName, string? outputSchemaVersion) =>
        string.IsNullOrWhiteSpace(outputSchemaVersion)
            ? throw new PromptTemplateException(
                resourceName, "'outputSchemaVersion' is required; a template states which schema it produces.")
            : outputSchemaVersion;

    private static PromptSafetyClass RequireSafetyClass(string resourceName, PromptSafetyClass safetyClass) =>
        safetyClass is PromptSafetyClass.Unspecified
            ? throw new PromptTemplateException(
                resourceName,
                "'safetyClass' is required. Choose the food-domain caution that applies deliberately — "
                + "omitting it would otherwise mean the least restrictive class by default.")
            : safetyClass;

    private static IReadOnlyList<PromptTemplateInput> RequireInputs(
        string resourceName,
        IReadOnlyList<ManifestInput>? inputs)
    {
        if (inputs is null)
        {
            throw new PromptTemplateException(
                resourceName, "'inputs' is required; declare an empty array for a template that takes none.");
        }

        var declared = new List<PromptTemplateInput>(inputs.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var input in inputs)
        {
            if (string.IsNullOrEmpty(input.Name) || !IsCamelCase(input.Name))
            {
                throw new PromptTemplateException(
                    resourceName, $"input names must be camelCase letters and digits; found '{input.Name}'.");
            }

            if (!seen.Add(input.Name))
            {
                throw new PromptTemplateException(resourceName, $"input '{input.Name}' is declared twice.");
            }

            declared.Add(new PromptTemplateInput(input.Name, input.Required, input.Description));
        }

        return declared;
    }

    private static void RequireFilenameMatchesManifest(
        string resourceName,
        string id,
        PromptTemplateVersion version)
    {
        var expected = $"{id}-{version}{Extension}";

        if (!resourceName.EndsWith(expected, StringComparison.Ordinal))
        {
            throw new PromptTemplateException(
                resourceName, $"the file name must end with '{expected}' to match its manifest.");
        }
    }

    /// <summary>
    /// Every placeholder is declared and every declared input is used. The second half matters as much as the
    /// first: an input nothing reads is a rename that only half landed, and it would otherwise sit in the
    /// manifest looking like a contract.
    /// </summary>
    private static void RequirePlaceholdersAndInputsAgree(
        string resourceName,
        string body,
        IReadOnlyList<PromptTemplateInput> inputs)
    {
        var used = ExtractPlaceholders(resourceName, body);
        var declared = inputs.Select(input => input.Name).ToHashSet(StringComparer.Ordinal);

        var undeclared = used.Except(declared).Order(StringComparer.Ordinal).ToList();

        if (undeclared.Count > 0)
        {
            throw new PromptTemplateException(
                resourceName,
                $"the body uses placeholder(s) no input declares: {string.Join(", ", undeclared)}.");
        }

        var unused = declared.Except(used).Order(StringComparer.Ordinal).ToList();

        if (unused.Count > 0)
        {
            throw new PromptTemplateException(
                resourceName, $"input(s) declared but never used by the body: {string.Join(", ", unused)}.");
        }
    }

    private static HashSet<string> ExtractPlaceholders(string resourceName, string body)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var position = 0;

        while (true)
        {
            var open = body.IndexOf(PlaceholderOpen, position, StringComparison.Ordinal);

            if (open < 0)
            {
                return names;
            }

            var nameStart = open + PlaceholderOpen.Length;
            var close = body.IndexOf(PlaceholderClose, nameStart, StringComparison.Ordinal);

            if (close < 0)
            {
                throw new PromptTemplateException(
                    resourceName,
                    $"a '{PlaceholderOpen}' at offset {open} is never closed. An unclosed placeholder would "
                    + "otherwise reach the model as literal text.");
            }

            var name = body[nameStart..close];

            if (name.Length == 0 || !IsCamelCase(name))
            {
                throw new PromptTemplateException(
                    resourceName,
                    $"placeholder '{PlaceholderOpen}{name}{PlaceholderClose}' is not a camelCase name. "
                    + "Whitespace inside the braces is not tolerated, because it would never substitute.");
            }

            names.Add(name);
            position = close + PlaceholderClose.Length;
        }
    }

    private static string RequireChecksum(string resourceName, string? declared, string body)
    {
        var actual = ComputeChecksum(body);

        if (string.IsNullOrEmpty(declared))
        {
            throw new PromptTemplateException(
                resourceName, $"'bodyChecksum' is required; the body hashes to '{actual}'.");
        }

        if (!string.Equals(declared, actual, StringComparison.Ordinal))
        {
            throw new PromptTemplateException(
                resourceName,
                $"'bodyChecksum' is '{declared}' but the body hashes to '{actual}'. A prompt body cannot "
                + "change without its manifest changing in the same edit — bump 'version' as well if the "
                + "change alters behaviour.");
        }

        return actual;
    }

    private static bool IsDottedLowercase(string value)
    {
        if (value.StartsWith('.') || value.EndsWith('.') || value.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value)
        {
            var allowed = character is '.' or '-'
                || character is >= 'a' and <= 'z'
                || character is >= '0' and <= '9';

            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCamelCase(string value)
    {
        if (value[0] is < 'a' or > 'z')
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The front matter as written. Every member is nullable so a missing one fails with a reason.</summary>
    private sealed record Manifest(
        string? Id,
        string? Version,
        string? OutputSchemaVersion,
        PromptSafetyClass SafetyClass,
        IReadOnlyList<ManifestInput>? Inputs,
        string? BodyChecksum);

    private sealed record ManifestInput(string? Name, bool Required, string? Description);
}
