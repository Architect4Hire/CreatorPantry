using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>Exports <see cref="AiEditorialPackageOutputDocument"/> as the JSON Schema the prompt carries.</summary>
public static class AiEditorialPackageOutputSchema
{
    private static readonly JsonSerializerOptions SchemaJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly Lazy<string> Exported = new(() =>
        SchemaJson.GetJsonSchemaAsNode(typeof(AiEditorialPackageOutputDocument)).ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }));

    public static string Json => Exported.Value;
}
