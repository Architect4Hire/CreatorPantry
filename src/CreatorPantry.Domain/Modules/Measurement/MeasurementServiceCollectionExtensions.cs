using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Measurement.Data;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Modules.Measurement;

public static class MeasurementServiceCollectionExtensions
{
    /// <summary>
    /// Registers the read-only unit catalogue seam, facade through repository. Requires
    /// <c>CreatorPantryDbContext</c> and <c>AddPaging</c>.
    /// </summary>
    /// <remarks>
    /// Nothing here is workspace-aware, and nothing here needs to be: the unit catalogue is the shared
    /// platform data zone, so this seam resolves and runs with no <c>IWorkspaceContext</c> at all.
    /// </remarks>
    public static IServiceCollection AddMeasurementModule(this IServiceCollection services)
    {
        services.AddPaging();
        services.AddScoped<IValidator<MeasurementUnitQueryViewModel>, MeasurementUnitQueryViewModelValidator>();
        services.AddScoped<IMeasurementFacade, MeasurementFacade>();
        services.AddScoped<IMeasurementBusiness, MeasurementBusiness>();
        services.AddScoped<IMeasurementDataLayer, MeasurementDataLayer>();
        services.AddScoped<IMeasurementUnitRepository, MeasurementUnitRepository>();

        return services;
    }
}
