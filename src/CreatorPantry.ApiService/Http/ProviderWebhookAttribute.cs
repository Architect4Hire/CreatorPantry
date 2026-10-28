namespace CreatorPantry.ApiService.Http;

/// <summary>
/// Marks an action as a payment or other provider's webhook (AP.6.2): anonymous, exempt from antiforgery at the
/// gateway, and authenticated by the provider's signature header alone. Read by the OpenAPI document, so it describes
/// the header the route takes instead of an antiforgery token it does not.
/// </summary>
/// <param name="signatureHeader">The header the provider signs each delivery in.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ProviderWebhookAttribute(string signatureHeader) : Attribute
{
    public string SignatureHeader { get; } = signatureHeader;
}
