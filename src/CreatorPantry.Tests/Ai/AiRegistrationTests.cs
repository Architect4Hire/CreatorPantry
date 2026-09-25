using CreatorPantry.AiProvider;
using CreatorPantry.Domain.Managers.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// What a host gets from <see cref="AiProviderRegistration.AddCreatorPantryAi"/>: the configured deployment
/// where the AppHost supplied one, a client that fails loudly where it did not, and a startup failure outside
/// development. These run against a bare host builder because that is exactly what the Worker is, and the
/// API's own wiring is covered separately below.
/// </summary>
public sealed class AiRegistrationTests
{
    private const string FoundryLocalConnectionString = TestDatabase.ModelDeploymentConnectionString;

    [Fact]
    public void Chat_client_comes_from_the_configured_deployment()
    {
        using var host = BuildHost(chat: FoundryLocalConnectionString, embeddings: FoundryLocalConnectionString);

        var chat = host.Services.GetRequiredService<IChatClient>();

        Assert.IsNotType<UnconfiguredChatClient>(chat);
    }

    [Fact]
    public void Embedding_generator_comes_from_the_configured_deployment()
    {
        using var host = BuildHost(chat: FoundryLocalConnectionString, embeddings: FoundryLocalConnectionString);

        var embeddings = host.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        Assert.IsNotType<UnconfiguredEmbeddingGenerator>(embeddings);
    }

    [Fact]
    public void Development_host_without_deployments_still_starts()
    {
        using var host = BuildHost(chat: null, embeddings: null);

        Assert.IsType<UnconfiguredChatClient>(host.Services.GetRequiredService<IChatClient>());
        Assert.IsType<UnconfiguredEmbeddingGenerator>(
            host.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
    }

    [Fact]
    public async Task Unconfigured_chat_client_fails_loudly_rather_than_answering()
    {
        using var host = BuildHost(chat: null, embeddings: null);
        var chat = host.Services.GetRequiredService<IChatClient>();

        var failure = await Assert.ThrowsAsync<AiProviderNotConfiguredException>(() =>
            chat.GetResponseAsync([new ChatMessage(ChatRole.User, "Write a headnote.")], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("chat", failure.Capability);
    }

    [Fact]
    public async Task Unconfigured_embedding_generator_fails_loudly_rather_than_returning_a_vector()
    {
        using var host = BuildHost(chat: null, embeddings: null);
        var embeddings = host.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        var failure = await Assert.ThrowsAsync<AiProviderNotConfiguredException>(() =>
            embeddings.GenerateAsync(["sourdough"], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("embeddings", failure.Capability);
    }

    /// <summary>
    /// Chat and embeddings are independent resources, so one configured deployment must not be taken away by
    /// the other's absence — pointing chat at a local model long before there is a vector column to fill is
    /// the normal intermediate state.
    /// </summary>
    [Fact]
    public void Each_deployment_is_decided_on_its_own()
    {
        using var host = BuildHost(chat: FoundryLocalConnectionString, embeddings: null);

        Assert.IsNotType<UnconfiguredChatClient>(host.Services.GetRequiredService<IChatClient>());
        Assert.IsType<UnconfiguredEmbeddingGenerator>(
            host.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
    }

    [Theory]
    [InlineData(null, FoundryLocalConnectionString)]
    [InlineData(FoundryLocalConnectionString, null)]
    [InlineData(null, null)]
    public void Missing_deployment_outside_development_refuses_to_start(string? chat, string? embeddings)
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            BuildHost(chat, embeddings, Environments.Production));

        Assert.Contains("No model deployment is configured", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fully_configured_host_outside_development_starts()
    {
        using var host = BuildHost(
            chat: FoundryLocalConnectionString,
            embeddings: FoundryLocalConnectionString,
            environment: Environments.Production);

        Assert.IsNotType<UnconfiguredChatClient>(host.Services.GetRequiredService<IChatClient>());
    }

    private static IHost BuildHost(
        string? chat,
        string? embeddings,
        string environment = "Development")
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environment,
            Args = [],
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{AiModelConnections.Chat}"] = chat,
            [$"ConnectionStrings:{AiModelConnections.Embeddings}"] = embeddings,
        });

        builder.AddCreatorPantryAi();

        return builder.Build();
    }
}
