using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreatorPantry.Domain.Managers.Quantities;

/// <summary>Reads and writes <see cref="DisplayPrecision"/> as a plain JSON number, e.g. <c>2</c>.</summary>
public sealed class DisplayPrecisionJsonConverter : JsonConverter<DisplayPrecision>
{
    public override DisplayPrecision Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            return new DisplayPrecision(reader.GetInt32());
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, DisplayPrecision value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.Value);
}
