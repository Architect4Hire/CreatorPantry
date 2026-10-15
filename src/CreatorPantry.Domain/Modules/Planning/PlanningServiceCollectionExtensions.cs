using CreatorPantry.Domain.Modules.Planning.Business;
using CreatorPantry.Domain.Modules.Planning.Data;
using CreatorPantry.Domain.Modules.Planning.Facade;
using CreatorPantry.Domain.Modules.Planning.Managers;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Modules.Planning;

public static class PlanningServiceCollectionExtensions
{
    /// <summary>
    /// The planning module's composition root. Requires
    /// <see cref="CreatorPantry.Domain.Managers.Persistence.CreatorPantryDbContext"/>, tenancy, application
    /// time and the idempotency helper to be registered.
    /// </summary>
    /// <remarks>
    /// A work task points at records four other modules own — <strong>Recipes</strong>, <strong>Content</strong>,
    /// <strong>Media</strong> and <strong>Workflows</strong> — and checks each through that module's facade
    /// before writing a link, so those four belong beside this call in any host that serves the task routes.
    /// It reads the workspace's scheduling zone through <strong>Brand</strong> for the same reason. A host
    /// missing one of them fails on the task facade at resolution rather than quietly at runtime.
    /// </remarks>
    public static IServiceCollection AddPlanningModule(this IServiceCollection services)
    {
        services.AddScoped<IWorkTaskRepository, WorkTaskRepository>();
        services.AddScoped<IWorkTaskDataLayer, WorkTaskDataLayer>();
        services.AddScoped<IWorkTaskBusiness, WorkTaskBusiness>();
        services.AddScoped<IWorkTaskFacade, WorkTaskFacade>();
        services.AddScoped<IValidator<CreateWorkTaskViewModel>, CreateWorkTaskViewModelValidator>();

        return services;
    }
}
