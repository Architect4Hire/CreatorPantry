using System.Net.Http.Headers;
using CreatorPantry.Domain.Modules.Publishing.Gateways;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Publishing.Integration.Buffer;

/// <summary>
/// Wires Buffer as this host's publishing provider, when it has been given one (INT-030/031/032/033).
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
/// <strong>Beside the module rather than in a host, because two hosts need it.</strong> The API confirms and
/// cancels publications; the Worker reconciles the ones the provider has not accounted for. One registration
/// means the two cannot drift into talking to Buffer differently — in particular, cannot drift on which
/// handler is allowed to retry a mutation.
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
    /// <param name="configuration">
    /// Read for <c>ConnectionStrings:publishing-buffer</c> only. Taking the configuration rather than the host
    /// builder is what lets this live beside the module and still be called from either host.
    /// </param>
    public static IServiceCollection AddBufferPublishing(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = BufferPublishingOptions.Parse(
            configuration.GetConnectionString(BufferPublishingOptions.ConnectionName));

        if (options is null)
        {
            return services;
        }

        services.AddSingleton(options);

        // RemoveAllResilienceHandlers is marked experimental (EXTEXP0001); it is the supported opt-out from
        // what ServiceDefaults applies to every HttpClient.
#pragma warning disable EXTEXP0001
        services
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

        // Registered after the module's TryAdd or before it, these are the gateways that resolve.
        services.AddScoped<IPublishingProfileGateway>(provider => new BufferPublishingProfileGateway(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(BufferPublishingOptions.ConnectionName),
            options,
            provider.GetRequiredService<ILogger<BufferPublishingProfileGateway>>()));

        // The same client and the same credential, for the mutation side (INT-031). It shares the client
        // precisely because the resilience handler is already removed from it: nothing may retry a createPost.
        services.AddScoped<IPublishingPostGateway>(provider => new BufferPublishingPostGateway(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(BufferPublishingOptions.ConnectionName),
            options,
            provider.GetRequiredService<ILogger<BufferPublishingPostGateway>>()));

        return services;
    }
}
