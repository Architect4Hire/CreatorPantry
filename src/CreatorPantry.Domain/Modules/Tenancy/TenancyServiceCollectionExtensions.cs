using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Business;
using CreatorPantry.Domain.Modules.Tenancy.Data;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Modules.Tenancy;

public static class TenancyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the request-scoped workspace context and the workspace resolution seam (facade through
    /// repository). No route or middleware calls the resolution facade yet. Requires
    /// <see cref="CreatorPantry.Domain.Managers.Persistence.CreatorPantryDbContext"/> to be registered.
    /// </summary>
    public static IServiceCollection AddTenancy(this IServiceCollection services)
    {
        // One WorkspaceContext instance per scope backs both interfaces, so a resolution made through the
        // resolver is visible through the read side for the rest of that scope.
        services.AddScoped<WorkspaceContext>();
        services.AddScoped<IWorkspaceContext>(provider => provider.GetRequiredService<WorkspaceContext>());
        services.AddScoped<IWorkspaceContextResolver>(provider => provider.GetRequiredService<WorkspaceContext>());

        services.AddScoped<IValidator<ResolveWorkspaceViewModel>, ResolveWorkspaceViewModelValidator>();
        services.AddScoped<IValidator<CreateWorkspaceViewModel>, CreateWorkspaceViewModelValidator>();
        services.AddScoped<IValidator<UpdateWorkspaceViewModel>, UpdateWorkspaceViewModelValidator>();
        services.AddScoped<IWorkspaceResolutionFacade, WorkspaceResolutionFacade>();
        services.AddScoped<IWorkspaceFacade, WorkspaceFacade>();
        services.AddScoped<IWorkspaceBusiness, WorkspaceBusiness>();
        services.AddScoped<IWorkspaceDataLayer, WorkspaceDataLayer>();
        services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();

        return services;
    }
}
