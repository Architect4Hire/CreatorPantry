using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreatorPantry.Domain.Managers.Quantities;

/// <summary>Reads and writes <see cref="QualitativeQuantity"/> as a plain JSON string, e.g. <c>"to taste"</c>.</summary>
public sealed class QualitativeQuantityJsonConverter : JsonConverter<QualitativeQuantity>
{
    public override QualitativeQuantity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var descriptor = reader.GetString()
            ?? throw new JsonException("A qualitative quantity must be a JSON string.");

        try
        {
            return new QualitativeQuantity(descriptor);
        }
        catch (ArgumentException ex)
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, QualitativeQuantity value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Descriptor);
}
