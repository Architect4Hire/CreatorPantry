using CreatorPantry.Domain.Managers.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The real ApiService host registers the model abstractions. AiRegistrationTests covers what
/// <c>AddCreatorPantryAi</c> decides; this covers that <c>Program</c> actually calls it, and that a host with
/// no model deployment still starts — which is what keeps every other endpoint test free of one.
/// </summary>
public sealed class ApiAiRegistrationTests
{
    [Fact]
    public async Task Api_host_starts_without_a_model_deployment_and_resolves_both_abstractions()
    {
        await using var host = await SqliteApiHost.StartAsync();

        Assert.IsType<UnconfiguredChatClient>(host.Factory.Services.GetRequiredService<IChatClient>());
        Assert.IsType<UnconfiguredEmbeddingGenerator>(
            host.Factory.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
    }
}
