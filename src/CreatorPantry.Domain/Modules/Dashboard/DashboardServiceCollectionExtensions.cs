using CreatorPantry.Domain.Modules.Dashboard.Business;
using CreatorPantry.Domain.Modules.Dashboard.Facade;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Modules.Dashboard;

public static class DashboardServiceCollectionExtensions
{
    /// <summary>
    /// The workspace dashboard (13.11).
    /// </summary>
    /// <remarks>
    /// It reads every other module through its facade, so a host registers it after them: the recipe, AI,
    /// content, media, publishing and brand modules, and the readiness seam with its own prerequisites. The API
    /// does; the worker has no dashboard and does not.
    /// </remarks>
    public static IServiceCollection AddDashboardModule(this IServiceCollection services)
    {
        services.AddScoped<IDashboardBusiness, DashboardBusiness>();
        services.AddScoped<IDashboardFacade, DashboardFacade>();

        return services;
    }
}
