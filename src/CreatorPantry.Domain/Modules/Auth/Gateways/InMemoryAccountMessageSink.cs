using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Domain.Modules.Auth.Gateways;

/// <summary>
/// Development-only sender that keeps the most recent messages in memory. It never logs message content.
/// </summary>
public sealed class InMemoryAccountMessageSink : IAccountMessageSender
{
    public const int Capacity = 50;

    private readonly Lock _gate = new();
    private readonly LinkedList<AccountMessage> _messages = new();

    /// <summary>Messages, newest first.</summary>
    public IReadOnlyList<AccountMessage> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. _messages];
            }
        }
    }

    public Task SendAsync(AccountMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _messages.AddFirst(message);
            while (_messages.Count > Capacity)
            {
                _messages.RemoveLast();
            }
        }

        return Task.CompletedTask;
    }
}

public static class AccountMessageServiceCollectionExtensions
{
    /// <summary>Registers <see cref="InMemoryAccountMessageSink"/> as the sender. Development only.</summary>
    public static IServiceCollection AddDevelopmentAccountMessageSink(this IServiceCollection services)
    {
        services.TryAddSingleton<InMemoryAccountMessageSink>();
        services.TryAddSingleton<IAccountMessageSender>(provider => provider.GetRequiredService<InMemoryAccountMessageSink>());

        return services;
    }
}
