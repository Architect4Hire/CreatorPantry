using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreatorPantry.Domain.Managers.Patching;

/// <summary>
/// Supplies the converter for any closed <see cref="PatchField{T}"/>. Attached to the type itself, so a
/// patch document behaves the same in the API, in the tests, and anywhere else — there is no registration
/// to add to a serializer and therefore none to forget, which would turn every explicit clear into a
/// deserialization failure.
/// </summary>
public sealed class PatchFieldJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(PatchField<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(PatchFieldJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
}

/// <summary>
/// Reads one field of a patch document. Presence is the signal, so this converter only ever runs for a field
/// the caller actually wrote — an absent one never reaches here and stays
/// <see cref="PatchField{T}.Absent"/>.
/// </summary>
internal sealed class PatchFieldJsonConverter<T> : JsonConverter<PatchField<T>>
{
    /// <summary>
    /// Required, and the single most load-bearing line here.
    /// </summary>
    /// <remarks>
    /// Without it the serializer handles a <c>null</c> token itself and never calls <see cref="Read"/>, so
    /// <c>"headnote": null</c> would leave the field at its default — absent. An explicit clear would then
    /// read as "not mentioned", which is the exact confusion <see cref="PatchField{T}"/> exists to prevent,
    /// and it would fail silently: the creator's cleared field would simply keep its old text.
    /// </remarks>
    public override bool HandleNull => true;

    public override PatchField<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        PatchField<T>.Submitted(JsonSerializer.Deserialize<T>(ref reader, options)!);

    /// <summary>Never. A patch document describes a request; responses are ServiceModels.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    /// <remarks>
    /// A converter cannot omit the property it is writing, so there is no honest way to spell "absent" on
    /// the wire — any value written would be a different request from the one being serialized. Throwing
    /// says so at the first attempt rather than producing a plausible-looking document that means something
    /// else.
    /// </remarks>
    public override void Write(Utf8JsonWriter writer, PatchField<T> value, JsonSerializerOptions options) =>
        throw new NotSupportedException(
            "A patch field cannot be serialized: an absent field has no representation, so writing one would "
            + "change what the document asks for.");
}
