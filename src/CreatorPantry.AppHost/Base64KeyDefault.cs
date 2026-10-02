using System.Security.Cryptography;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;

namespace CreatorPantry.AppHost;

/// <summary>
/// Generates random key material, base64-encoded — the shape <c>IdempotencyOptions.IsValidKey</c> requires of
/// <c>Idempotency:FingerprintKey</c>.
/// </summary>
/// <remarks>
/// Not <see cref="GenerateParameterDefault"/>: that produces a 22-character password drawn from letters, digits
/// and punctuation, which is neither base64 nor 32 bytes, so the API and Worker would reject it at startup.
/// </remarks>
internal sealed class Base64KeyDefault(int byteLength = 32) : ParameterDefault
{
    public override string GetDefaultValue() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteLength));

    public override void WriteToManifest(ManifestPublishingContext context)
    {
        context.Writer.WriteStartObject("generate");
        context.Writer.WriteString("type", "base64-key");
        context.Writer.WriteNumber("byteLength", byteLength);
        context.Writer.WriteEndObject();
    }
}
