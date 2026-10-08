using CreatorPantry.AiProvider;
using CreatorPantry.Domain.Managers.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CreatorPantry.Tests.Ai;

// MEAI001: see UnconfiguredImageGenerator for why this is suppressed per file rather than project-wide.
#pragma warning disable MEAI001

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

    private const string ImagesConnectionString =
        "Endpoint=https://venice.example/api/v1;Key=images-test-key;Model=image-model-test";

    /// <summary>
    /// The model id is what provenance records as the model, so it has to survive the trip from the
    /// connection string to the generator's own metadata — the gateway reads it from there and nowhere else.
    /// </summary>
    [Fact]
    public void Image_generator_comes_from_the_configured_deployment_and_names_it()
    {
        using var host = BuildHost(chat: null, embeddings: null, images: ImagesConnectionString);

        var images = host.Services.GetRequiredService<IImageGenerator>();
        var metadata = Assert.IsType<ImageGeneratorMetadata>(images.GetService(typeof(ImageGeneratorMetadata)));

        Assert.NotNull(images.GetService(typeof(VeniceImageGenerator)));
        Assert.Equal("image-model-test", metadata.DefaultModelId);
        Assert.Equal(VeniceImageGenerator.ProviderName, metadata.ProviderName);
        Assert.Equal(new Uri("https://venice.example/api/v1/"), metadata.ProviderUri);
    }

    [Fact]
    public async Task Host_without_an_image_deployment_gets_the_generator_that_refuses()
    {
        using var host = BuildHost(chat: FoundryLocalConnectionString, embeddings: FoundryLocalConnectionString);
        var images = host.Services.GetRequiredService<IImageGenerator>();

        Assert.IsType<UnconfiguredImageGenerator>(images);

        var failure = await Assert.ThrowsAsync<AiProviderNotConfiguredException>(() =>
            images.GenerateAsync(new ImageGenerationRequest("a loaf of bread"), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("images", failure.Capability);
    }

    /// <summary>
    /// Images are a third independent decision: configuring them must not supply chat or embeddings, and
    /// their absence must not take either away.
    /// </summary>
    [Fact]
    public void Images_are_decided_on_their_own()
    {
        using var imagesOnly = BuildHost(chat: null, embeddings: null, images: ImagesConnectionString);

        Assert.IsType<UnconfiguredChatClient>(imagesOnly.Services.GetRequiredService<IChatClient>());
        Assert.NotNull(imagesOnly.Services.GetRequiredService<IImageGenerator>().GetService(typeof(VeniceImageGenerator)));

        using var imagesMissing = BuildHost(chat: FoundryLocalConnectionString, embeddings: FoundryLocalConnectionString);

        Assert.IsNotType<UnconfiguredChatClient>(imagesMissing.Services.GetRequiredService<IChatClient>());
        Assert.IsType<UnconfiguredImageGenerator>(imagesMissing.Services.GetRequiredService<IImageGenerator>());
    }

    /// <summary>
    /// Unlike chat and embeddings, a missing image deployment is not a reason to refuse to start: it turns
    /// one feature off, and stopping the host over it would turn the other two off as well.
    /// </summary>
    [Fact]
    public void Missing_image_deployment_outside_development_still_starts()
    {
        using var host = BuildHost(
            chat: FoundryLocalConnectionString,
            embeddings: FoundryLocalConnectionString,
            environment: Environments.Production);

        Assert.IsType<UnconfiguredImageGenerator>(host.Services.GetRequiredService<IImageGenerator>());
    }

    /// <summary>
    /// A connection string missing a part would otherwise start cleanly and fail every generation with a
    /// status that reads like a provider outage. The refusal names the part and never quotes the key.
    /// </summary>
    [Theory]
    [InlineData("Endpoint=https://venice.example/api/v1;Key=images-test-key", "names no model")]
    [InlineData("Endpoint=https://venice.example/api/v1;Model=image-model-test", "names no key")]
    [InlineData("Key=images-test-key;Model=image-model-test", "names no endpoint")]
    [InlineData("Endpoint=not a url;Key=images-test-key;Model=image-model-test", "not an absolute URL")]
    public void Image_connection_string_missing_a_part_refuses_to_start(string images, string expected)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => BuildHost(
            chat: null,
            embeddings: null,
            images: images));

        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("images-test-key", failure.Message, StringComparison.Ordinal);
    }

    private static IHost BuildHost(
        string? chat,
        string? embeddings,
        string environment = "Development",
        string? images = null)
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
            [$"ConnectionStrings:{AiModelConnections.Images}"] = images,
        });

        builder.AddCreatorPantryAi();

        return builder.Build();
    }
}
