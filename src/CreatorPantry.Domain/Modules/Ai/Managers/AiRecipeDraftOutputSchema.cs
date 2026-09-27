using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The JSON Schema a model is shown for AIREC-002, generated from <see cref="AiRecipeDraftOutputDocument"/> the
/// same way <see cref="AiOutputSchema"/> is generated from the recipe-diff document — exported rather than
/// hand-written, so the shape a model is asked for and the shape its answer is judged against cannot drift
/// apart.
/// </summary>
public static class AiRecipeDraftOutputSchema
{
    private static readonly JsonSerializerOptions SchemaJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly Lazy<string> Exported = new(() =>
        SchemaJson.GetJsonSchemaAsNode(typeof(AiRecipeDraftOutputDocument)).ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>The schema, as indented JSON suitable for embedding in a prompt.</summary>
    public static string Json => Exported.Value;
}
