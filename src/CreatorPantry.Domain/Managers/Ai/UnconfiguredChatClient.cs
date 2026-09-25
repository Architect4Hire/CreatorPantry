using Microsoft.Extensions.AI;

namespace CreatorPantry.Domain.Managers.Ai;

/// <summary>
/// The <see cref="IChatClient"/> a development host registers when no chat deployment is configured.
/// </summary>
/// <remarks>
/// <para>
/// It exists so a host with no model account still starts, so that <c>aspire run</c>, endpoint tests and
/// <c>WebApplicationFactory</c> do not require a model deployment to exercise anything else. Every call
/// throws <see cref="AiProviderNotConfiguredException"/>.
/// </para>
/// <para>
/// It deliberately does <em>not</em> return canned text. A stub that answered would put fabricated prose on
/// the same path a creator's accepted draft travels, and the only thing distinguishing it from a real
/// generation would be a configuration value nobody reads at review time. Failing loudly keeps the
/// unconfigured host honest, and a developer who wants real local generation enables Foundry Local instead.
/// </para>
/// </remarks>
public sealed class UnconfiguredChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw Unconfigured();

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw Unconfigured();

    /// <summary>Advertises no underlying provider service, which is the accurate answer here.</summary>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceType.IsInstanceOfType(this) && serviceKey is null ? this : null;
    }

    public void Dispose()
    {
    }

    private static AiProviderNotConfiguredException Unconfigured() => new("chat");
}
