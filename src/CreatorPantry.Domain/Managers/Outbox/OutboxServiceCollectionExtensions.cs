using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Managers.Outbox;

public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IOutboxWriter"/> and <see cref="IOutboxDispatcher"/>. Requires
    /// <c>CreatorPantryDbContext</c> and <c>IClock</c>. Register concrete <see cref="IOutboxMessageHandler"/>
    /// implementations separately with keyed DI, one per message type.
    /// </summary>
    public static IServiceCollection AddOutbox(this IServiceCollection services)
    {
        services.AddScoped<IOutboxWriter, OutboxWriter>();
        services.AddScoped<IOutboxDispatcher, OutboxDispatcher>();

        return services;
    }
}
