using CreatorPantry.Domain.Auth;
using CreatorPantry.Domain.Data;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.MigrationService;

public static class MigrationDatabaseExtensions
{
    /// <summary>Registers the application DbContext through the Aspire SQL Server integration.</summary>
    public static IHostApplicationBuilder AddMigrationDatabase(this IHostApplicationBuilder builder)
    {
        // Not AddSqlServerDbContext: that helper pools contexts, and a pooled context's activator resolves
        // against the root container, so it cannot take a scoped constructor dependency. CreatorPantryDbContext
        // takes an optional IWorkspaceContext for its per-request query filter (see ApiService/Program.cs for
        // the same rationale) — nothing here registers AddTenancy today, so pooling happens to be harmless as
        // written, but that would silently break the moment this host ever needs scoped tenancy state, so both
        // hosts stay on the same non-pooled registration by construction rather than by convention.
        builder.Services.AddDbContext<CreatorPantryDbContext>(options =>
            options.UseSqlServer(builder.Configuration.GetConnectionString(CreatorPantryDbContext.ConnectionName)));
        builder.EnrichSqlServerDbContext<CreatorPantryDbContext>();

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
