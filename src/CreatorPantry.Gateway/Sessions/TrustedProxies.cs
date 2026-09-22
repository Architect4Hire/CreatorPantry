using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace CreatorPantry.Gateway.Sessions;

/// <summary>
/// Client address and scheme when the gateway runs behind a load balancer or TLS-terminating ingress.
/// <c>X-Forwarded-For</c>/<c>X-Forwarded-Proto</c> are honored only from proxies listed in
/// <c>Edge:TrustedProxies</c> (addresses) or <c>Edge:TrustedNetworks</c> (CIDR ranges), one hop deep. With
/// none configured, forwarded headers are never applied: the connection's own address is the client.
/// </summary>
public static class TrustedProxies
{
    public static IHostApplicationBuilder AddTrustedProxies(this IHostApplicationBuilder builder)
    {
        var proxies = Parse(builder.Configuration, "Edge:TrustedProxies", IPAddress.Parse);
        var networks = Parse(builder.Configuration, "Edge:TrustedNetworks", System.Net.IPNetwork.Parse);
        var trust = new ForwardedHeaderTrust(proxies.Count > 0 || networks.Count > 0);
        builder.Services.AddSingleton(trust);

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;

            // Replace the loopback defaults with exactly the configured proxies. Note that ASP.NET treats two
            // empty lists as "trust every proxy", which is why the middleware only runs when trust.Enabled.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            proxies.ForEach(proxy => options.KnownProxies.Add(proxy));
            networks.ForEach(network => options.KnownIPNetworks.Add(network));
        });

        if (!trust.Enabled && !builder.Environment.IsDevelopment())
        {
            // Not fatal (the gateway may face clients directly), but behind a load balancer every client would
            // share the balancer's address and the per-IP sign-in limit would throttle everyone together.
            builder.Services.AddHostedService<NoTrustedProxyWarning>();
        }

        return builder;
    }

    /// <summary>
    /// Applies forwarded headers only when trusted proxies are configured, and never for a connection with no
    /// remote address (ASP.NET skips its known-proxy check in that case, for example on a Unix socket).
    /// </summary>
    public static WebApplication UseTrustedForwardedHeaders(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<ForwardedHeaderTrust>().Enabled)
        {
            return app;
        }

        app.Use((context, next) =>
        {
            if (context.Connection.RemoteIpAddress is null)
            {
                context.Request.Headers.Remove(ForwardedHeadersDefaults.XForwardedForHeaderName);
                context.Request.Headers.Remove(ForwardedHeadersDefaults.XForwardedProtoHeaderName);
            }

            return next(context);
        });
        app.UseForwardedHeaders();

        return app;
    }

    private static List<T> Parse<T>(IConfiguration configuration, string key, Func<string, T> parse)
    {
        var values = configuration.GetSection(key).Get<string[]>() ?? [];
        return values.Select(value =>
        {
            try
            {
                return parse(value);
            }
            catch (FormatException)
            {
                throw new InvalidOperationException($"{key} contains '{value}', which is not a valid entry.");
            }
        }).ToList();
    }

    internal sealed record ForwardedHeaderTrust(bool Enabled);

    private sealed class NoTrustedProxyWarning(ILogger<NoTrustedProxyWarning> logger) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogWarning("No Edge:TrustedProxies or Edge:TrustedNetworks configured. If the gateway runs behind "
                + "a load balancer, client addresses and HTTPS detection will be wrong.");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
