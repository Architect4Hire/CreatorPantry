using System.Net.Http.Headers;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ingredients.Gateways;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Ingredients.Integration.UsdaFdc;

/// <summary>
/// Wires FoodData Central as this host's evidence provider, when it has been given a key (14.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The key arrives as one connection string</strong>, <c>ConnectionStrings:evidence-usda-fdc</c> —
/// <c>Endpoint=…;Key=…</c> — which the AppHost composes from a secret parameter. It is read once, here, put on
/// one named client's <c>X-Api-Key</c> header, and held nowhere a URL, a log line or an API response could pick
/// it up.
/// </para>
/// <para>
/// <strong>Absent or unusable, nothing is registered</strong> and the module's own "not configured" gateway
/// stands. Evidence is optional (B-09); its absence is never a reason for a host not to boot.
/// </para>
/// <para>
/// <strong>The resilience defaults are removed, not tuned.</strong> The standard handler retries a 429, and a
/// request sent into an api.data.gov block extends nothing but the block. The gateway retries what is safe to,
/// within bounds it can state.
/// </para>
/// </remarks>
public static class UsdaFdcEvidenceRegistration
{
    /// <param name="configuration">Read for <c>ConnectionStrings:evidence-usda-fdc</c> only.</param>
    public static IServiceCollection AddUsdaFdcEvidence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = UsdaFdcEvidenceOptions.Parse(
            configuration.GetConnectionString(UsdaFdcEvidenceOptions.ConnectionName));

        if (options is null)
        {
            return services;
        }

        services.AddSingleton(options);
        services.AddSingleton(provider => new UsdaFdcThrottle(provider.GetRequiredService<IClock>()));

        // RemoveAllResilienceHandlers is marked experimental (EXTEXP0001); it is the supported opt-out from
        // what ServiceDefaults applies to every HttpClient.
#pragma warning disable EXTEXP0001
        services
            .AddHttpClient(UsdaFdcEvidenceOptions.ConnectionName, client =>
            {
                client.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                // The backstop for a caller that brought no bound of its own. The gateway bounds each attempt
                // well inside this.
                client.Timeout = TimeSpan.FromSeconds(60);
            })
            // No redirects: the key is a header on every request, and nothing should carry it to an address
            // other than the configured one. A 3xx is read as an answer this contract does not understand.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        // Registered after the module's TryAdd or before it, this is the gateway that resolves.
        services.AddScoped<IReferenceEvidenceGateway>(provider => new UsdaFdcEvidenceGateway(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(UsdaFdcEvidenceOptions.ConnectionName),
            options,
            provider.GetRequiredService<UsdaFdcThrottle>(),
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<ILogger<UsdaFdcEvidenceGateway>>()));

        return services;
    }
}
