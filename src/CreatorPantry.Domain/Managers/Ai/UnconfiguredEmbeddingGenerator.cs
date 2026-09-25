using Microsoft.Extensions.AI;

namespace CreatorPantry.Domain.Managers.Ai;

/// <summary>
/// The embedding generator a development host registers when no embedding deployment is configured.
/// </summary>
/// <remarks>
/// Every call throws <see cref="AiProviderNotConfiguredException"/>. Returning zero or random vectors would
/// be worse than failing: they would persist into the vector column as real embeddings, and nothing later
/// could tell a meaningless neighbour from a meaningful one. See <see cref="UnconfiguredChatClient"/>.
/// </remarks>
public sealed class UnconfiguredEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) => throw Unconfigured();

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceType.IsInstanceOfType(this) && serviceKey is null ? this : null;
    }

    public void Dispose()
    {
    }

    private static AiProviderNotConfiguredException Unconfigured() => new("embeddings");
}
