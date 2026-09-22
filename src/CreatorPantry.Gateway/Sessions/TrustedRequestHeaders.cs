using Yarp.ReverseProxy.Transforms;

namespace CreatorPantry.Gateway.Sessions;

/// <summary>
/// Only an allowlist of caller headers reaches the API. Everything else a browser sends, including identity
/// and forwarding headers such as <c>Authorization</c>, <c>Cookie</c>, <c>Forwarded</c>, <c>X-Real-IP</c>,
/// <c>X-Forwarded-User</c>, or <c>X-MS-CLIENT-PRINCIPAL</c>, is dropped. The gateway then adds the trusted
/// values itself: <c>X-Forwarded-For/Proto/Host</c> (route transforms) and the internal token.
/// </summary>
public static class TrustedRequestHeaders
{
    /// <summary>
    /// Caller request headers forwarded to the API. Extend deliberately: a header added here becomes input the
    /// API may trust. Content headers (<c>Content-Type</c>, <c>Content-Length</c>) travel with the body and are
    /// not affected.
    /// </summary>
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Accept",
        "Accept-Encoding",
        "Accept-Language",
        "User-Agent",
        "If-Match",
        "If-None-Match",
        "If-Modified-Since",
        "If-Unmodified-Since",
        "Idempotency-Key",
        "traceparent",
        "tracestate",
    };

    /// <summary>Headers the gateway itself sets on the proxied request; never taken from the caller.</summary>
    private static readonly IReadOnlySet<string> GatewayOwned = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "X-Forwarded-For",
        "X-Forwarded-Proto",
        "X-Forwarded-Host",
    };

    public static IReverseProxyBuilder AddTrustedHeaderTransform(this IReverseProxyBuilder builder) =>
        builder.AddTransforms(context => context.AddRequestTransform(transform =>
        {
            var headers = transform.ProxyRequest.Headers;
            foreach (var name in headers.Select(header => header.Key).ToList())
            {
                if (!Allowed.Contains(name) && !GatewayOwned.Contains(name))
                {
                    headers.Remove(name);
                }
            }

            return ValueTask.CompletedTask;
        }));
}
