using CreatorPantry.Domain.Modules.Auth;
using CreatorPantry.Domain.Modules.Auth.Managers;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Reference;
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

        // The sample ingredient set cites a development seed source and is not reference material, so it stops
        // at the Production boundary. The unit and vocabulary catalogue crosses it: those are definitional
        // rather than licensed, and a database without measurement units cannot resolve a recipe line at all.
        builder.Services.AddCreatorPantrySchema(
            includeDevelopmentSampleData: !builder.Environment.IsProduction(),
            configuration: builder.Configuration);

        return builder;
    }

    /// <summary>
    /// Applies CreatorPantry migrations, then idempotent seed data: the PlatformAdmin role, any configured ops
    /// API clients, and the global reference catalogue.
    /// </summary>
    /// <param name="includeDevelopmentSampleData">
    /// Whether the reference seeder also writes the sample ingredient set. Defaults to <c>false</c> so a caller
    /// that has not thought about it gets the production-safe tier.
    /// </param>
    /// <param name="configuration">
    /// Read for <c>Ops:Clients</c> (baseline B-14). Optional, and with it omitted no ops client is provisioned
    /// at all — which is the right default: a deployment gets an operator credential only by asking for one.
    /// </param>
    public static IServiceCollection AddCreatorPantrySchema(
        this IServiceCollection services,
        bool includeDevelopmentSampleData = false,
        IConfiguration? configuration = null)
    {
        services.AddDbContextMigration<CreatorPantryDbContext>();
        services.AddPlatformRoleSeeding(configuration);
        services.AddReferenceSeeding(includeDevelopmentSampleData);

        return services;
    }
}
