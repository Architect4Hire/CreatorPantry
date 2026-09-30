using System.Text.Json;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// Reads the parts of a stored <c>content.seo-package.v1</c> document that structured data may carry. Tolerant
/// by design: a missing or differently shaped part yields nothing rather than an exception, because an
/// export must never fail on, or make up for, a document it cannot read.
/// </summary>
public static class SeoPackageDocumentReader
{
    public const string SchemaVersion = "content.seo-package.v1";

    public static (string? MetaDescription, IReadOnlyList<string> KeyPhrases) Read(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return (null, []);
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var version)
                || version.ValueKind != JsonValueKind.String
                || version.GetString() != SchemaVersion
                || !root.TryGetProperty("sections", out var sections)
                || sections.ValueKind != JsonValueKind.Object)
            {
                return (null, []);
            }

            string? description = null;
            if (sections.TryGetProperty("metaDescription", out var meta)
                && meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(text.GetString()))
            {
                description = text.GetString();
            }

            var phrases = new List<string>();
            if (sections.TryGetProperty("keyPhrases", out var keys) && keys.ValueKind == JsonValueKind.Array)
            {
                foreach (var key in keys.EnumerateArray())
                {
                    if (key.ValueKind == JsonValueKind.Object
                        && key.TryGetProperty("phrase", out var phrase)
                        && phrase.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(phrase.GetString()))
                    {
                        phrases.Add(phrase.GetString()!);
                    }
                }
            }

            return (description, phrases);
        }
        catch (JsonException)
        {
            return (null, []);
        }
    }
}
