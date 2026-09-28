using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The JSON Schema a model is shown for AIREC-004, generated from
/// <see cref="AiSubstitutionOutputDocument"/> the same way <see cref="AiConceptOutputSchema"/> is generated
/// from AIREC-001's document — exported rather than hand-written, so the shape a model is asked for and the
/// shape its answer is judged against cannot drift apart.
/// </summary>
/// <remarks>
/// Exporting matters more here than anywhere else in the module. The claims AIREC-004 must never make are
/// prevented by enum members that do not exist, and a hand-written schema listing <c>"Removes"</c> among the
/// allergen effects — by habit, or because it looked like it belonged — would invite exactly the answer the
/// type was shaped to refuse.
/// </remarks>
public static class AiSubstitutionOutputSchema
{
    private static readonly JsonSerializerOptions SchemaJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly Lazy<string> Exported = new(() =>
        SchemaJson.GetJsonSchemaAsNode(typeof(AiSubstitutionOutputDocument)).ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>The schema, as indented JSON suitable for embedding in a prompt.</summary>
    public static string Json => Exported.Value;
}
