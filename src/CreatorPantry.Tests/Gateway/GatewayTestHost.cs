extern alias Gateway;

using System.Net;
using System.Security.Claims;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Yarp.ReverseProxy.Forwarder;

namespace CreatorPantry.Tests.Gateway;

/// <summary>
/// Shared configuration for in-process gateway hosts: test signing key, service discovery for "api", an
/// in-memory ticket store and key ring in place of Redis, and optional in-memory downstreams.
/// </summary>
internal static class GatewayTestHost
{
    /// <summary>Session and antiforgery cookies are Secure, so clients must use an https base address.</summary>
    public static readonly WebApplicationFactoryClientOptions HttpsClient = new() { BaseAddress = new Uri("https://localhost") };

    /// <summary>The connection address every test request arrives from, as Kestrel would report it.</summary>
    public static readonly IPAddress DefaultConnectionAddress = IPAddress.Parse("203.0.113.5");

    /// <summary>Test-only request header that sets the simulated connection address (never reaches the app).</summary>
    public const string ConnectionAddressHeader = "X-Test-Connection-Address";

    public static WebApplicationFactory<Gateway::Program> Create(
        Func<HttpMessageHandler>? proxyDownstream = null,
        Func<HttpMessageHandler>? apiSessions = null,
        ClaimsPrincipal? sessionUser = null,
        Action<IWebHostBuilder>? configure = null) =>
        new WebApplicationFactory<Gateway::Program>().WithWebHostBuilder(web =>
        {
            web.UseSetting("services:api:https:0", "https://api.test");
            web.UseSetting("services:api:http:0", "http://api.test");
            web.UseSetting("InternalToken:SigningKeyPem", TestKeyPair.Shared.PrivateKeyPem);
            web.UseSetting("ConnectionStrings:cache", "localhost:1,abortConnect=false");
            web.UseSetting("Aspire:StackExchange:Redis:DisableHealthChecks", "true");

            web.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new ConnectionAddressStartupFilter());
                services.RemoveAll<IDistributedCache>();
                services.AddDistributedMemoryCache();
                services.PostConfigure<KeyManagementOptions>(options => options.XmlRepository = new InMemoryXmlRepository());

                if (proxyDownstream is not null)
                {
                    services.AddSingleton<IForwarderHttpClientFactory>(new InMemoryForwarderFactory(proxyDownstream));
                }

                if (apiSessions is not null)
                {
                    services.Configure<HttpClientFactoryOptions>(
                        typeof(Gateway::CreatorPantry.Gateway.Sessions.ApiSessionClient).Name,
                        options => options.HttpMessageHandlerBuilderActions.Add(handlers => handlers.PrimaryHandler = apiSessions()));
                }

                if (sessionUser is not null)
                {
                    // Stands in for the BFF cookie session in proxy-only tests.
                    services.AddSingleton<IStartupFilter>(new SessionUserStartupFilter(sessionUser));
                }
            });

            configure?.Invoke(web);
        });

    private sealed class InMemoryForwarderFactory(Func<HttpMessageHandler> downstream) : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(downstream(), disposeHandler: true);
    }

    private sealed class InMemoryXmlRepository : IXmlRepository
    {
        private readonly List<XElement> _elements = [];

        public IReadOnlyCollection<XElement> GetAllElements()
        {
            lock (_elements)
            {
                return [.. _elements];
            }
        }

        public void StoreElement(XElement element, string friendlyName)
        {
            lock (_elements)
            {
                _elements.Add(element);
            }
        }
    }

    /// <summary>TestServer leaves the remote address empty; real Kestrel connections always have one.</summary>
    private sealed class ConnectionAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                var simulated = context.Request.Headers[ConnectionAddressHeader].ToString();
                context.Request.Headers.Remove(ConnectionAddressHeader);
                context.Connection.RemoteIpAddress = string.IsNullOrEmpty(simulated)
                    ? DefaultConnectionAddress
                    : IPAddress.Parse(simulated);
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private sealed class SessionUserStartupFilter(ClaimsPrincipal user) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.User = user;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
