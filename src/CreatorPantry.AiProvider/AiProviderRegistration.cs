using System.Data.Common;
using System.Net.Http.Headers;
using CreatorPantry.Domain.Managers.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.AiProvider;

// MEAI001: see UnconfiguredImageGenerator for why this is suppressed per file rather than project-wide.
#pragma warning disable MEAI001

/// <summary>
/// Wires <see cref="Microsoft.Extensions.AI.IChatClient"/>,
/// <see cref="Microsoft.Extensions.AI.IEmbeddingGenerator{TInput,TEmbedding}"/> and
/// <see cref="Microsoft.Extensions.AI.IImageGenerator"/> for a host, from whichever model deployments the
/// AppHost has supplied.
/// </summary>
public static class AiProviderRegistration
{
    /// <summary>
    /// Registers the model abstractions: the configured Microsoft Foundry deployment where the AppHost has
    /// supplied one, the unconfigured fallback in development where it has not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each deployment is decided on its own, because they are independent resources. Configuring chat
    /// without embeddings is a real intermediate state — chat models are cheap to point at Foundry Local,
    /// embeddings are not useful until there is a vector column to fill — and one missing deployment must
    /// not take the other's client away.
    /// </para>
    /// <para>
    /// Outside development a missing deployment is a startup failure rather than a fallback. The fallback
    /// exists so hosts without a model account still start; a deployment that silently went missing in a
    /// deployed environment is a misconfiguration, and discovering it when a creator's generation throws is
    /// strictly worse than discovering it when the host refuses to start. This mirrors
    /// <c>AddDevelopmentAccountMessageSink</c>, where the development-only sink is also the only case in
    /// which the real provider may be absent.
    /// </para>
    /// <para>
    /// No credential for chat or embeddings is read here. In run mode the Foundry Local connection string
    /// carries its own key; in Azure mode the connection string carries no key at all and the client falls
    /// back to the ambient Azure credential. Neither path puts a secret in configuration this project owns.
    /// The images key is the exception: Venice has no ambient credential, so it is read out of the
    /// connection string the AppHost composed and set on that one HTTP client's headers.
    /// </para>
    /// </remarks>
    public static IHostApplicationBuilder AddCreatorPantryAi(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (IsConfigured(builder, AiModelConnections.Chat))
        {
            // Registers a non-keyed IChatClient over a ChatCompletionsClient, reading the endpoint,
            // credential and deployment name out of the connection string. Also registers the
            // Microsoft.Extensions.AI trace sources and meters, so AI calls correlate with the rest of the
            // request without a ServiceDefaults change.
            builder.AddAzureChatCompletionsClient(AiModelConnections.Chat).AddChatClient();
        }
        else
        {
            RequireDevelopment(builder, AiModelConnections.Chat);
            builder.Services.AddUnconfiguredChatClient();
        }

        if (IsConfigured(builder, AiModelConnections.Embeddings))
        {
            builder.AddAzureEmbeddingsClient(AiModelConnections.Embeddings).AddEmbeddingGenerator();
        }
        else
        {
            RequireDevelopment(builder, AiModelConnections.Embeddings);
            builder.Services.AddUnconfiguredEmbeddingGenerator();
        }

        // Images are decided on their own too, but a missing deployment is not a startup failure anywhere:
        // without chat or embeddings the product does not work, without images one feature reports
        // provider-not-configured, and refusing to start over that would take the other two down with it.
        // The unconfigured generator is what makes the Media module's gateway report NotConfigured, which
        // its job settles as a terminal failure with that category.
        if (IsConfigured(builder, AiModelConnections.Images))
        {
            var images = ImagesConnection(builder);

            // Venice.ai, over plain HTTP (B-15 as amended for Venice): Azure.AI.Inference, which serves chat
            // and embeddings, has no image route, and Venice's own needs no SDK. A named client, so the key
            // is on this one client's headers and nowhere a different HttpClient could pick it up.
            //
            // The resilience defaults are removed, not tuned. The standard handler gives an attempt ten
            // seconds and then retries it, and an image takes longer than that: every generation would be
            // abandoned, repeated and charged for three times over. The gateway above already bounds the
            // call and the operation above that already decides whether to try again.
            // RemoveAllResilienceHandlers is marked experimental (EXTEXP0001); it is the supported opt-out
            // from what ServiceDefaults applies to every HttpClient.
#pragma warning disable EXTEXP0001
            builder.Services
                .AddHttpClient(AiModelConnections.Images, client =>
                {
                    client.BaseAddress = images.Endpoint;
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", images.Key);

                    // Longer than MediaPolicy.GenerationProviderTimeout on purpose, so the gateway's bound is
                    // the one that fires and this is only the backstop for a caller that brought none.
                    client.Timeout = TimeSpan.FromMinutes(5);
                })
                .RemoveAllResilienceHandlers()
                .AddTypedClient<IImageGenerator>((client, services) =>
                    new VeniceImageGenerator(
                            client, images.Model, services.GetRequiredService<ILogger<VeniceImageGenerator>>())
                        .AsBuilder()
                        .UseOpenTelemetry(services.GetRequiredService<ILoggerFactory>())
                        .Build(services));
#pragma warning restore EXTEXP0001
        }
        else
        {
            builder.Services.AddUnconfiguredImageGenerator();
        }

        return builder;
    }

    private static bool IsConfigured(IHostApplicationBuilder builder, string connectionName) =>
        !string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString(connectionName));

    /// <summary>
    /// The endpoint, key and model out of the images connection string.
    /// </summary>
    /// <remarks>
    /// All three or a refusal to start. A string missing one is a misconfiguration to stop on: the
    /// alternative is a host that starts and then fails every generation with a 401 or a 400 that reads like
    /// a provider outage. The message names the part that is missing and never quotes the string, which
    /// holds the key.
    /// </remarks>
    private static (Uri Endpoint, string Key, string Model) ImagesConnection(IHostApplicationBuilder builder)
    {
        var connection = new DbConnectionStringBuilder
        {
            ConnectionString = builder.Configuration.GetConnectionString(AiModelConnections.Images),
        };

        // A trailing slash, so the request path is appended to the endpoint's own path rather than replacing
        // its last segment — "https://api.venice.ai/api/v1" would otherwise lose the "v1".
        return Uri.TryCreate(Part("Endpoint").TrimEnd('/') + "/", UriKind.Absolute, out var endpoint)
            ? (endpoint, Part("Key"), Part("Model"))
            : throw new InvalidOperationException(
                $"ConnectionStrings:{AiModelConnections.Images} has an endpoint that is not an absolute URL.");

        string Part(string name) =>
            connection.TryGetValue(name, out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
                ? text.Trim()
                : throw new InvalidOperationException(
                    $"ConnectionStrings:{AiModelConnections.Images} names no {name.ToLowerInvariant()}. It "
                    + "takes the form 'Endpoint=https://api.venice.ai/api/v1;Key=<key>;Model=<model id>'.");
    }

    private static void RequireDevelopment(IHostApplicationBuilder builder, string connectionName)
    {
        if (builder.Environment.IsDevelopment())
        {
            return;
        }

        throw new InvalidOperationException(
            $"No model deployment is configured for '{connectionName}'. Supply "
            + $"ConnectionStrings:{connectionName}, or run in Development, where the unconfigured client is "
            + "registered so a host without a model account can still start.");
    }
}
