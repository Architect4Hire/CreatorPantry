using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Domain.Managers.Ai;

/// <summary>
/// Registers the provider-neutral model abstractions a host falls back to when it has no deployment.
/// </summary>
/// <remarks>
/// The host chooses the provider, exactly as it chooses the cache store — see
/// <see cref="Caching.CachingServiceCollectionExtensions.AddApplicationCache"/>. This assembly only knows
/// <see cref="IChatClient"/> and <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> and <see cref="IImageGenerator"/>, never a provider SDK,
/// so the fallbacks live here and the real clients are wired in <c>CreatorPantry.AiProvider</c>.
/// </remarks>
public static class AiServiceCollectionExtensions
{
    public static IServiceCollection AddUnconfiguredChatClient(this IServiceCollection services)
    {
        services.TryAddSingleton<IChatClient>(new UnconfiguredChatClient());

        return services;
    }

    public static IServiceCollection AddUnconfiguredEmbeddingGenerator(this IServiceCollection services)
    {
        services.TryAddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new UnconfiguredEmbeddingGenerator());

        return services;
    }

    // MEAI001: see UnconfiguredImageGenerator for why this is suppressed here rather than project-wide.
#pragma warning disable MEAI001
    public static IServiceCollection AddUnconfiguredImageGenerator(this IServiceCollection services)
    {
        services.TryAddSingleton<IImageGenerator>(new UnconfiguredImageGenerator());

        return services;
    }
#pragma warning restore MEAI001
}
