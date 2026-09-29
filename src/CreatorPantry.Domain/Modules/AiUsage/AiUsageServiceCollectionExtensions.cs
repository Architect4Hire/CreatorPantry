using CreatorPantry.Domain.Modules.AiUsage.Business;
using CreatorPantry.Domain.Modules.AiUsage.Data;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Modules.AiUsage;

public static class AiUsageServiceCollectionExtensions
{
    /// <summary>
    /// Registers the per-account AI usage recording seam (facade through repository). Requires
    /// <see cref="CreatorPantry.Domain.Managers.Persistence.CreatorPantryDbContext"/> to be registered.
    /// </summary>
    /// <remarks>
    /// Scoped, like every other seam here, and for one extra reason worth stating: the recording path stages
    /// onto the caller's unit of work, so it must share the caller's <c>DbContext</c> instance. A singleton or
    /// transient registration would still compile and would stage onto a context nobody saves.
    /// </remarks>
    public static IServiceCollection AddAiUsageModule(this IServiceCollection services)
    {
        services.AddScoped<IAiUsageRecordingFacade, AiUsageRecordingFacade>();
        services.AddScoped<IAiUsageRecordingBusiness, AiUsageRecordingBusiness>();
        services.AddScoped<IAiUsageDataLayer, AiUsageDataLayer>();
        services.AddScoped<IAiUsageRepository, AiUsageRepository>();

        return services;
    }
}
