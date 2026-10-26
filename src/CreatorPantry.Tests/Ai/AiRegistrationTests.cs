using CreatorPantry.AiProvider;
using CreatorPantry.Domain.Managers.Ai;
using CreatorPantry.Tests.AiUsage;
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

    /// <summary>
    /// Outside development the chat model must be rated and priced (AP.2.1), so the shared fixture deployment
    /// carries both unless a case says otherwise. The figures are arbitrary; only their presence is read here.
    /// </summary>
    private static readonly Dictionary<string, string?> RatedFixtureModel = new()
    {
        ["Ai:Quota:ModelRates:phi-4-mini-instruct-generic-gpu:CreditsPerMillionInputTokens"] = "1",
        ["Ai:Quota:ModelRates:phi-4-mini-instruct-generic-gpu:CreditsPerMillionOutputTokens"] = "1",
        ["Ai:Cost:Models:phi-4-mini-instruct-generic-gpu:InputPerMillionTokens"] = "0.001",
        ["Ai:Cost:Models:phi-4-mini-instruct-generic-gpu:OutputPerMillionTokens"] = "0.001",
    };

    private const string AzureChat =
        "Endpoint=https://example.openai.azure.com/openai/deployments/{0};Key=chat-test-key;DeploymentId={0}";

    /// <summary>
    /// An unrated chat model starts and settles every call at the flat estimate, with its cost unknown. Outside
    /// development that is found at startup, by name, rather than on an invoice.
    /// </summary>
    [Fact]
    public void Unrated_chat_model_outside_development_refuses_to_start()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => BuildHost(
            chat: string.Format(AzureChat, "gpt-9-unpriced"),
            embeddings: FoundryLocalConnectionString,
            environment: Environments.Production));

        Assert.Contains("'gpt-9-unpriced'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Ai:Quota:ModelRates", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Ai:Cost:Models", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("chat-test-key", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A rate with no price, or a price with no rate, is half a configuration, and is named as such.</summary>
    [Fact]
    public void A_chat_model_rated_but_not_priced_names_the_missing_half()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => BuildHost(
            // A model the shipped tables do not hold: the host also reads the output folder's appsettings.json.
            chat: string.Format(AzureChat, "gpt-test-rated-only"),
            embeddings: FoundryLocalConnectionString,
            environment: Environments.Production,
            settings: new()
            {
                ["Ai:Quota:ModelRates:gpt-test-rated-only:CreditsPerMillionInputTokens"] = "250",
                ["Ai:Quota:ModelRates:gpt-test-rated-only:CreditsPerMillionOutputTokens"] = "1562.5",
            }));

        Assert.Contains("Ai:Cost:Models", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Ai:Quota:ModelRates", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The deployed configuration as shipped: the API's own rate table rates the model the Azure deployment
    /// runs, under its plain name and under a dated deployment name alike.
    /// </summary>
    [Theory]
    [InlineData("gpt-5.4-nano")]
    [InlineData("gpt-4o-mini-2024-07-18")]
    public void The_shipped_rate_table_lets_a_rated_deployment_start(string deployment)
    {
        using var host = BuildHost(
            chat: string.Format(AzureChat, deployment),
            embeddings: FoundryLocalConnectionString,
            environment: Environments.Production,
            settings: ModelRateTableTests.ShippedRateSettings());

        Assert.IsNotType<UnconfiguredChatClient>(host.Services.GetRequiredService<IChatClient>());
    }

    /// <summary>A deployment named for its role rather than its model says which model it runs.</summary>
    [Fact]
    public void The_declared_model_is_checked_instead_of_the_deployment_name()
    {
        using var host = BuildHost(
            chat: string.Format(AzureChat, "chat"),
            embeddings: FoundryLocalConnectionString,
            environment: Environments.Production,
            settings: new(ModelRateTableTests.ShippedRateSettings()) { ["Ai:Gateway:ModelName"] = "gpt-5.4-nano" });

        Assert.IsNotType<UnconfiguredChatClient>(host.Services.GetRequiredService<IChatClient>());
    }

    /// <summary>
    /// The deployment is read from whichever of DeploymentId, Deployment or Model the connection string uses —
    /// Aspire's own parser refuses a string with more than one. A string naming none cannot be checked, and says
    /// how to fix that.
    /// </summary>
    [Theory]
    [InlineData("Endpoint=https://example.openai.azure.com;Key=k;DeploymentId=gpt-5.4-nano", null)]
    [InlineData("Endpoint=https://example.openai.azure.com;Key=k;Deployment=gpt-5.4-nano", null)]
    [InlineData("Endpoint=https://example.openai.azure.com;Key=k;Model=gpt-5.4-nano", null)]
    [InlineData("Endpoint=https://example.openai.azure.com;Key=k", "names no deployment")]
    public void The_chat_deployment_is_read_from_the_connection_string(string chat, string? refusal)
    {
        IHost Build() => BuildHost(
            chat: chat,
            embeddings: FoundryLocalConnectionString,
            environment: Environments.Production,
            settings: ModelRateTableTests.ShippedRateSettings());

        if (refusal is null)
        {
            using var host = Build();
            Assert.IsNotType<UnconfiguredChatClient>(host.Services.GetRequiredService<IChatClient>());
            return;
        }

        var failure = Assert.Throws<InvalidOperationException>(() => Build());
        Assert.Contains(refusal, failure.Message, StringComparison.Ordinal);
        Assert.Contains("Ai:Gateway:ModelName", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Development may try a model before anyone has priced it; its calls settle at the estimate.</summary>
    [Fact]
    public void Unrated_chat_model_in_development_still_starts()
    {
        using var host = BuildHost(
            chat: string.Format(AzureChat, "gpt-9-unpriced"),
            embeddings: FoundryLocalConnectionString,
            rateFixtureModel: false);

        Assert.IsNotType<UnconfiguredChatClient>(host.Services.GetRequiredService<IChatClient>());
    }

    private static IHost BuildHost(
        string? chat,
        string? embeddings,
        string environment = "Development",
        string? images = null,
        Dictionary<string, string?>? settings = null,
        bool rateFixtureModel = true)
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

        if (rateFixtureModel)
        {
            builder.Configuration.AddInMemoryCollection(RatedFixtureModel);
        }

        builder.Configuration.AddInMemoryCollection(settings ?? []);
        builder.AddCreatorPantryAi();

        return builder.Build();
    }
}
