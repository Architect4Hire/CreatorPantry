using System.Net.Http.Headers;
using CreatorPantry.Domain.Modules.Publishing.Gateways;
using CreatorPantry.Domain.Modules.Publishing.Integration.Buffer;

namespace CreatorPantry.ApiService.Integration;

/// <summary>
/// Wires Buffer as this host's publishing provider, when it has been given one (INT-030).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The credential arrives as one connection string</strong>, <c>ConnectionStrings:publishing-buffer</c>
/// — <c>Endpoint=…;Key=…;Workspace=&lt;workspace id&gt;</c> — which the AppHost composes from a secret parameter. It is read
/// once, here, put on one named client's headers, and held nowhere a different <c>HttpClient</c>, a log line
/// or an API response could pick it up.
/// </para>
/// <para>
/// <strong>Absent or unusable, nothing is registered</strong> and the module's own "not configured" gateway
/// stands: the host starts, and every workspace reads a stable degraded answer. A provider is never a reason
/// for a host not to boot.
/// </para>
/// <para>
/// <strong>The resilience defaults are removed, not tuned.</strong> The standard handler retries every
/// request, POSTs included, and Buffer's API is GraphQL: one endpoint, always POST. A read is safe to repeat
/// and the gateway repeats it itself, within bounds it can state; a mutation, which arrives with scheduling,
/// is not, and must never be retried by a handler that cannot tell the two apart.
/// </para>
/// </remarks>
public static class BufferPublishingRegistration
{
    public static IHostApplicationBuilder AddBufferPublishing(this IHostApplicationBuilder builder)
    {
        var options = BufferPublishingOptions.Parse(
            builder.Configuration.GetConnectionString(BufferPublishingOptions.ConnectionName));

        if (options is null)
        {
            return builder;
        }

        builder.Services.AddSingleton(options);

        // RemoveAllResilienceHandlers is marked experimental (EXTEXP0001); it is the supported opt-out from
        // what ServiceDefaults applies to every HttpClient.
#pragma warning disable EXTEXP0001
        builder.Services
            .AddHttpClient(BufferPublishingOptions.ConnectionName, client =>
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                // The backstop for a caller that brought no bound of its own. The gateway bounds each attempt
                // well inside this.
                client.Timeout = TimeSpan.FromSeconds(60);
            })
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        // Registered after the module's TryAdd or before it, this is the gateway that resolves.
        builder.Services.AddScoped<IPublishingProfileGateway>(services => new BufferPublishingProfileGateway(
            services.GetRequiredService<IHttpClientFactory>().CreateClient(BufferPublishingOptions.ConnectionName),
            options,
            services.GetRequiredService<ILogger<BufferPublishingProfileGateway>>()));

        return builder;
    }
}
