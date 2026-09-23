using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Managers.Audit;

public static class AuditServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IAuditWriter"/>. Requires <c>CreatorPantryDbContext</c> and <c>IClock</c>.</summary>
    public static IServiceCollection AddAudit(this IServiceCollection services)
    {
        services.AddScoped<IAuditWriter, AuditWriter>();

        return services;
    }
}
