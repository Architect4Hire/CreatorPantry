using System.Text.Json;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// Reads a stored <c>content.editorial-package.v1</c> document. Tolerant by design, as
/// <see cref="SeoPackageDocumentReader"/> is: a missing or differently shaped part yields nothing rather than
/// an exception, because an export must never fail on, or make up for, a document it cannot read.
/// </summary>
public static class EditorialPackageDocumentReader
{
    public const string SchemaVersion = "content.editorial-package.v1";

    public static AcceptedEditorialServiceModel Read(int revisionNumber, string? content, bool isCurrent)
    {
        var empty = new AcceptedEditorialServiceModel(revisionNumber, null, null, [], [], null, [], null, isCurrent);

        if (string.IsNullOrWhiteSpace(content))
        {
            return empty;
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
                return empty;
            }

            return new AcceptedEditorialServiceModel(
                revisionNumber,
                Text(sections, "headnote"),
                Text(sections, "introduction"),
                Items(sections, "tips", item => TextOf(item)),
                Items(sections, "substitutions", item =>
                    item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("lineId", out var id) && id.ValueKind == JsonValueKind.String
                    && Guid.TryParse(id.GetString(), out var lineId)
                    && String(item, "suggestion") is { } suggestion
                    && String(item, "culinaryNote") is { } note
                        ? new AcceptedEditorialSubstitution(lineId, suggestion, note)
                        : null),
                Text(sections, "storageReheating"),
                Items(sections, "faq", item =>
                    item.ValueKind == JsonValueKind.Object
                    && String(item, "question") is { } question
                    && String(item, "answer") is { } answer
                        ? new AcceptedEditorialFaq(question, answer)
                        : null),
                Text(sections, "cta"),
                isCurrent);
        }
        catch (JsonException)
        {
            return empty;
        }
    }

    private static string? Text(JsonElement sections, string name) =>
        sections.TryGetProperty(name, out var element) ? TextOf(element) : null;

    private static string? TextOf(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object ? String(element, "text") : null;

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static IReadOnlyList<T> Items<T>(JsonElement sections, string name, Func<JsonElement, T?> read)
        where T : class
    {
        if (!sections.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. array.EnumerateArray().Select(read).OfType<T>()];
    }
}
