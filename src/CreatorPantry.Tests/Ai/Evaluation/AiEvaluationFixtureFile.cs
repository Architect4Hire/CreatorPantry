using System.Text.Json;
using System.Text.Json.Serialization;
using CreatorPantry.Domain.Managers.Prompts;

namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>Reads one <c>.eval.json</c> fixture file.</summary>
/// <remarks>
/// Plain JSON rather than <c>.prompt.md</c>'s front-matter-plus-body shape: a fixture has no prose body to
/// checksum, only structured input and an expected outcome, so front matter would be ceremony wrapped around
/// what is already the whole file.
/// </remarks>
public static class AiEvaluationFixtureFile
{
    public const string Extension = ".eval.json";

    private static readonly JsonSerializerOptions FixtureJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },

        // A mistyped property (e.g. "categry") would otherwise be silently ignored, leaving Category
        // Unspecified-shaped and the resulting error about a missing field rather than the typo in front of
        // the author.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <param name="resourceName">
    /// The embedded resource name, which must end in <c>{id}-{version}.eval.json</c> — checked against the
    /// manifest so a file copied for a new version, with its id left behind, cannot load under the wrong
    /// identity.
    /// </param>
    /// <exception cref="AiEvaluationException">The file is malformed or mis-declared.</exception>
    public static AiEvaluationFixture Parse(string resourceName, string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);
        ArgumentNullException.ThrowIfNull(content);

        var manifest = Deserialize(resourceName, content);

        var id = RequireId(resourceName, manifest.Id);
        var version = RequireVersion(resourceName, manifest.Version);
        var category = RequireCategory(resourceName, manifest.Category);
        var kind = RequireKind(resourceName, manifest.Kind);
        var description = RequireDescription(resourceName, manifest.Description);

        RequireFilenameMatchesManifest(resourceName, id, version);
        RequirePresent(resourceName, "input", manifest.Input);
        RequirePresent(resourceName, "expect", manifest.Expect);

        return new AiEvaluationFixture(id, version, category, kind, description, manifest.Input, manifest.Expect);
    }

    private static Manifest Deserialize(string resourceName, string content)
    {
        try
        {
            return JsonSerializer.Deserialize<Manifest>(content, FixtureJson)
                ?? throw new AiEvaluationException(resourceName, "the file is null.");
        }
        catch (JsonException exception)
        {
            throw new AiEvaluationException(resourceName, $"not valid JSON. {exception.Message}");
        }
    }

    private static string RequireId(string resourceName, string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? throw new AiEvaluationException(resourceName, "'id' is required.")
            : id;

    private static PromptTemplateVersion RequireVersion(string resourceName, string? version) =>
        PromptTemplateVersion.TryParse(version, out var parsed)
            ? parsed
            : throw new AiEvaluationException(
                resourceName, $"'version' must be major.minor.patch; found '{version}'.");

    private static AiEvaluationCategory RequireCategory(string resourceName, AiEvaluationCategory? category) =>
        category ?? throw new AiEvaluationException(resourceName, "'category' is required.");

    private static AiEvaluationKind RequireKind(string resourceName, AiEvaluationKind? kind) =>
        kind ?? throw new AiEvaluationException(resourceName, "'kind' is required.");

    private static string RequireDescription(string resourceName, string? description) =>
        string.IsNullOrWhiteSpace(description)
            ? throw new AiEvaluationException(
                resourceName, "'description' is required: what this fixture demonstrates, in a sentence.")
            : description;

    private static void RequirePresent(string resourceName, string property, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined)
        {
            throw new AiEvaluationException(resourceName, $"'{property}' is required.");
        }
    }

    private static void RequireFilenameMatchesManifest(
        string resourceName, string id, PromptTemplateVersion version)
    {
        var expected = $"{id}-{version}{Extension}";

        if (!resourceName.EndsWith(expected, StringComparison.Ordinal))
        {
            throw new AiEvaluationException(
                resourceName, $"the file name must end with '{expected}' to match its manifest.");
        }
    }

    /// <summary>The file as written. Every member is nullable so a missing one fails with a reason.</summary>
    private sealed record Manifest(
        string? Id,
        string? Version,
        AiEvaluationCategory? Category,
        AiEvaluationKind? Kind,
        string? Description,
        JsonElement Input,
        JsonElement Expect);
}
