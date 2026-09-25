using CreatorPantry.Domain.Managers.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace CreatorPantry.AiProvider;

/// <summary>
/// Wires <see cref="Microsoft.Extensions.AI.IChatClient"/> and
/// <see cref="Microsoft.Extensions.AI.IEmbeddingGenerator{TInput,TEmbedding}"/> for a host, from whichever
/// model deployments the AppHost has supplied.
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
    /// No credential is read here. In run mode the Foundry Local connection string carries its own key; in
    /// Azure mode the connection string carries no key at all and the client falls back to the ambient Azure
    /// credential. Neither path puts a secret in configuration this project owns.
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

        return builder;
    }

    private static bool IsConfigured(IHostApplicationBuilder builder, string connectionName) =>
        !string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString(connectionName));

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
