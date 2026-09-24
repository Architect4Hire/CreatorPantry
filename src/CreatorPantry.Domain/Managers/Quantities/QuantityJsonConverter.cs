using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreatorPantry.Domain.Managers.Quantities;

/// <summary>
/// Reads and writes <see cref="Quantity"/> as <c>{ "numerator": "…", "denominator": "…" }</c>, with both
/// numbers written as JSON strings rather than JSON numbers.
/// </summary>
/// <remarks>
/// A <see cref="BigInteger"/> can exceed JavaScript's 2^53 safe-integer range. Writing it as a JSON number
/// would let a browser silently decode a numerator or denominator it cannot represent exactly, corrupting the
/// one property — exactness — this type exists to guarantee (frontend.md: "decode unknown data at
/// boundaries").
/// </remarks>
public sealed class QuantityJsonConverter : JsonConverter<Quantity>
{
    public override Quantity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("A quantity must be a JSON object with \"numerator\" and \"denominator\".");

        BigInteger? numerator = null;
        BigInteger? denominator = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var propertyName = reader.GetString();
            reader.Read();

            switch (propertyName)
            {
                case "numerator":
                    numerator = ReadBigInteger(ref reader);
                    break;
                case "denominator":
                    denominator = ReadBigInteger(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (numerator is null || denominator is null)
            throw new JsonException("A quantity requires both \"numerator\" and \"denominator\".");

        try
        {
            return Quantity.FromFraction(numerator.Value, denominator.Value);
        }
        catch (ArgumentException ex)
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, Quantity value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("numerator", value.Numerator.ToString(CultureInfo.InvariantCulture));
        writer.WriteString("denominator", value.Denominator.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static BigInteger ReadBigInteger(ref Utf8JsonReader reader)
    {
        var text = reader.GetString()
            ?? throw new JsonException("A quantity's numerator and denominator must be JSON strings.");

        return BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new JsonException($"\"{text}\" is not a valid integer.");
    }
}
