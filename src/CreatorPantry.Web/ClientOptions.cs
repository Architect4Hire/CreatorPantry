namespace CreatorPantry.Web;

/// <summary>Browser-facing settings served to the SPA at /runtime-config.json.</summary>
public sealed class ClientOptions
{
    public const string SectionName = "Client";

    /// <summary>Public base URL of CreatorPantry.Gateway, the only backend origin the browser calls.</summary>
    public string GatewayUrl { get; set; } = string.Empty;

    /// <summary>
    /// The SPA's Content-Security-Policy: scripts only from this origin; API calls only to this origin and the
    /// gateway; Google Fonts for the design system's typefaces. <c>style-src 'unsafe-inline'</c> is required
    /// because Angular injects component styles at runtime; scripts remain strict.
    /// </summary>
    public string SpaContentSecurityPolicy()
    {
        var gatewayOrigin = new Uri(GatewayUrl).GetLeftPart(UriPartial.Authority);
        return string.Join("; ",
            "default-src 'self'",
            "script-src 'self'",
            "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com",
            "font-src 'self' https://fonts.gstatic.com",
            "img-src 'self' data: blob:",
            $"connect-src 'self' {gatewayOrigin}",
            "object-src 'none'",
            "base-uri 'self'",
            "form-action 'self'",
            "frame-ancestors 'none'");
    }

    public static bool IsAbsoluteHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}

/// <summary>The shape the SPA decodes; keep in sync with src/web/src/app/core/runtime-config.service.ts.</summary>
public sealed record RuntimeConfig(string GatewayUrl);
