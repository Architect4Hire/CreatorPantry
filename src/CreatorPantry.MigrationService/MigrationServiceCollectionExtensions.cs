using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.MigrationService;

public static class MigrationServiceCollectionExtensions
{
    /// <summary>Registers the migration host: the one-shot worker and its run outcome.</summary>
    public static IServiceCollection AddMigrationHost(this IServiceCollection services)
    {
        services.AddSingleton<MigrationRunState>();
        services.AddHostedService<MigrationWorker>();

        return services;
    }

    /// <summary>
    /// Adds <typeparamref name="TContext"/> as a migration target. The context itself is registered
    /// separately (through the Aspire SQL Server integration).
    /// </summary>
    public static IServiceCollection AddDbContextMigration<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.AddScoped<IMigrationTarget, DbContextMigrationTarget<TContext>>();

        return services;
    }
}
