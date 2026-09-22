using CreatorPantry.Domain.Business.Tenancy;
using CreatorPantry.Domain.Data.Repositories;
using CreatorPantry.Domain.Data.Tenancy;
using CreatorPantry.Domain.Facade.Tenancy;
using CreatorPantry.Domain.Models.ViewModels.Tenancy;
using CreatorPantry.Domain.Validation.Tenancy;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Tenancy;

public static class TenancyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the request-scoped workspace context and the workspace resolution seam (facade through
    /// repository). No route or middleware calls the resolution facade yet. Requires
    /// <see cref="Data.CreatorPantryDbContext"/> to be registered.
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
