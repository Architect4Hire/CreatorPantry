using System.Security.Cryptography;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;

namespace CreatorPantry.AppHost;

/// <summary>
/// Generates a random ECDSA P-256 private key, PKCS8 PEM-encoded — the shape
/// <c>ECDsa.ImportFromPem</c> expects on both sides of the gateway-to-API trust (baseline B-13).
/// </summary>
/// <remarks>
/// Only the private half is generated here. The public half the API verifies against is derived from it at
/// AppHost startup (see <c>DerivePublicKeyPem</c> in <c>AppHost.cs</c>) rather than generated as a second,
/// independent parameter — two independently-generated halves could drift into a mismatched pair, which would
/// fail every internal request's signature check with no obvious cause.
/// </remarks>
internal sealed class EcdsaP256PrivateKeyDefault : ParameterDefault
{
    public override string GetDefaultValue()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return ecdsa.ExportPkcs8PrivateKeyPem();
    }

    public override void WriteToManifest(ManifestPublishingContext context)
    {
        context.Writer.WriteStartObject("generate");
        context.Writer.WriteString("type", "ecdsa-p256-pkcs8-pem");
        context.Writer.WriteEndObject();
    }
}
