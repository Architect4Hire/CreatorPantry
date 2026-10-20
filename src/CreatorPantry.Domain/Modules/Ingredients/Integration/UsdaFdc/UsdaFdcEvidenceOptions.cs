namespace CreatorPantry.Domain.Modules.Ingredients.Integration.UsdaFdc;

/// <summary>
/// What a host needs to read FoodData Central, from one connection string.
/// </summary>
/// <param name="Endpoint">The API's base address, always ending in a slash so relative paths resolve beneath it.</param>
/// <param name="ApiKey">An api.data.gov key. A credential: never logged, never returned, never in a URL.</param>
public sealed record UsdaFdcEvidenceOptions(Uri Endpoint, string ApiKey)
{
    /// <summary>The connection string's name, and the named <c>HttpClient</c>'s.</summary>
    public const string ConnectionName = "evidence-usda-fdc";

    /// <summary>How long one attempt may take before it is abandoned.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The first wait between attempts; the second is twice it.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// How long the source is left alone after it throttles without saying for how long. FDC blocks a key for an
    /// hour once its address is over the hourly limit, so asking sooner only spends the next hour too.
    /// </summary>
    public TimeSpan DefaultThrottle { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Printed nowhere on purpose, but if it ever is, it is not with the key in it.</summary>
    public override string ToString() => $"UsdaFdcEvidenceOptions {{ Endpoint = {Endpoint} }}";

    /// <summary>
    /// Reads <c>Endpoint=…;Key=…</c>. Null when the value is absent or either part is missing or unusable: a
    /// half-configured provider is an unconfigured one, never a host that will not start.
    /// </summary>
    public static UsdaFdcEvidenceOptions? Parse(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = part.IndexOf('=', StringComparison.Ordinal);
            if (at > 0)
            {
                parts[part[..at].Trim()] = part[(at + 1)..].Trim();
            }
        }

        return parts.TryGetValue("Endpoint", out var endpoint)
            && parts.TryGetValue("Key", out var key)
            && key.Length > 0
            && Uri.TryCreate(endpoint.EndsWith('/') ? endpoint : endpoint + "/", UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.Query)
                ? new UsdaFdcEvidenceOptions(uri, key)
                : null;
    }
}
