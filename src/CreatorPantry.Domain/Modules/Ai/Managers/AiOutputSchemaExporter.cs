using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The one way an output document becomes the JSON Schema a model is shown, so every capability's schema is
/// exactly as strict as the validator that judges its answer.
/// </summary>
/// <remarks>
/// <para>
/// Every output validator deserializes with <see cref="JsonUnmappedMemberHandling.Disallow"/> and
/// <see cref="JsonSerializerOptions.RespectNullableAnnotations"/>: an unknown field or a null in a non-nullable
/// place is refused. Exported with the serializer's defaults, the schema said neither — it allowed any extra
/// property, a null concept, a null list item — so a model could satisfy the schema it was shown and still be
/// refused, spending the one correction a request gets. The exporter now states the same rules the validator
/// applies: <c>additionalProperties: false</c> on every object, and non-null wherever the type is non-null.
/// </para>
/// <para>
/// <see cref="JsonSchemaExporterOptions.TreatNullObliviousAsNonNullable"/> covers what the serializer cannot
/// read an annotation for — the root and a collection's items. No output document declares a nullable item.
/// Numbers are asked for as numbers: the validators still accept a numeric string (the web defaults), but
/// there is no reason to invite one.
/// </para>
/// <para>
/// This is shape only. The rules a validator's domain stage enforces — counts, distinct titles, index ranges —
/// are not expressible here and are still checked, told or not.
/// </para>
/// </remarks>
public static class AiOutputSchemaExporter
{
    private static readonly JsonSerializerOptions SchemaJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },

        // The exporter requires a resolver up front and will not fall back to the reflection-based default the
        // way ordinary serialization does, so it is named here rather than left to be supplied lazily.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        NumberHandling = JsonNumberHandling.Strict,
    };

    private static readonly JsonSchemaExporterOptions ExporterOptions = new()
    {
        TreatNullObliviousAsNonNullable = true,
    };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The schema for <paramref name="documentType"/>, as indented JSON suitable for a prompt.</summary>
    public static string Export(Type documentType)
    {
        ArgumentNullException.ThrowIfNull(documentType);

        return SchemaJson.GetJsonSchemaAsNode(documentType, ExporterOptions).ToJsonString(Indented);
    }
}
