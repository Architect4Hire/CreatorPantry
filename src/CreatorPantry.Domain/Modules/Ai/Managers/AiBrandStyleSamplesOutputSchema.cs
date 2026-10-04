using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>Exports <see cref="AiBrandStyleSamplesOutputDocument"/> as the JSON Schema the prompt carries.</summary>
/// <remarks>
/// One schema for both calls, because both calls are asked for the same three pieces. What differs between them
/// is the material in the prompt, never the shape of the answer — which is what makes the two halves comparable
/// at all.
/// </remarks>
public static class AiBrandStyleSamplesOutputSchema
{
    private static readonly JsonSerializerOptions SchemaJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly Lazy<string> Exported = new(() =>
        SchemaJson.GetJsonSchemaAsNode(typeof(AiBrandStyleSamplesOutputDocument)).ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }));

    public static string Json => Exported.Value;
}
