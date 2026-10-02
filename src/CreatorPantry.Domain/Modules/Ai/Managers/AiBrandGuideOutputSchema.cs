using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>Exports <see cref="AiBrandGuideOutputDocument"/> as the JSON Schema the prompt carries.</summary>
/// <remarks>
/// The document is the schema, so the shape a model is asked for and the shape it is judged against are one
/// definition. Editing the document changes this export, which means bumping the template's
/// <c>outputSchemaVersion</c>.
/// </remarks>
public static class AiBrandGuideOutputSchema
{
    private static readonly JsonSerializerOptions SchemaJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly Lazy<string> Exported = new(() =>
        SchemaJson.GetJsonSchemaAsNode(typeof(AiBrandGuideOutputDocument)).ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }));

    public static string Json => Exported.Value;
}
