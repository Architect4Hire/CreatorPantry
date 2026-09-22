using CreatorPantry.Domain.Auth;
using CreatorPantry.Domain.Data;

namespace CreatorPantry.MigrationService;

public static class MigrationDatabaseExtensions
{
    /// <summary>Registers the application DbContext through the Aspire SQL Server integration.</summary>
    public static IHostApplicationBuilder AddMigrationDatabase(this IHostApplicationBuilder builder)
    {
        builder.AddSqlServerDbContext<CreatorPantryDbContext>(CreatorPantryDbContext.ConnectionName);
        builder.Services.AddCreatorPantrySchema();

        return builder;
    }

    /// <summary>Applies CreatorPantry migrations, then idempotent seed data such as the PlatformAdmin role.</summary>
    public static IServiceCollection AddCreatorPantrySchema(this IServiceCollection services)
    {
        services.AddDbContextMigration<CreatorPantryDbContext>();
        services.AddPlatformRoleSeeding();

        return services;
    }
}
