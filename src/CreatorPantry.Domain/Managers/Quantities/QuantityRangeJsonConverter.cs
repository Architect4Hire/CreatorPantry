using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreatorPantry.Domain.Managers.Quantities;

/// <summary>
/// Reads and writes <see cref="QuantityRange"/> as <c>{ "lower": Quantity, "upper": Quantity }</c>, delegating
/// each bound to <see cref="QuantityJsonConverter"/>.
/// </summary>
public sealed class QuantityRangeJsonConverter : JsonConverter<QuantityRange>
{
    public override QuantityRange Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("A quantity range must be a JSON object with \"lower\" and \"upper\".");

        Quantity? lower = null;
        Quantity? upper = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var propertyName = reader.GetString();
            reader.Read();

            switch (propertyName)
            {
                case "lower":
                    lower = JsonSerializer.Deserialize<Quantity>(ref reader, options);
                    break;
                case "upper":
                    upper = JsonSerializer.Deserialize<Quantity>(ref reader, options);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (lower is null || upper is null)
            throw new JsonException("A quantity range requires both \"lower\" and \"upper\".");

        try
        {
            return QuantityRange.Create(lower.Value, upper.Value);
        }
        catch (ArgumentException ex)
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, QuantityRange value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("lower");
        JsonSerializer.Serialize(writer, value.Lower, options);
        writer.WritePropertyName("upper");
        JsonSerializer.Serialize(writer, value.Upper, options);
        writer.WriteEndObject();
    }
}
