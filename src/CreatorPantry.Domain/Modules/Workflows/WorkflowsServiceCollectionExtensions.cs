using CreatorPantry.Domain.Modules.Workflows.Business;
using CreatorPantry.Domain.Modules.Workflows.Data;
using CreatorPantry.Domain.Modules.Workflows.Facade;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Domain.Modules.Workflows;

public static class WorkflowsServiceCollectionExtensions
{
    /// <summary>Guided-workflow runs (13A.5).</summary>
    /// <remarks>
    /// Starting a run checks its starting record through the recipe, media and content modules and reads the brand
    /// profile for a workspace's time zone, so those modules must be registered too. Each is asked through its
    /// facade, and a missing one fails at container resolution rather than at the first start.
    /// </remarks>
    public static IServiceCollection AddWorkflowsModule(this IServiceCollection services)
    {
        // TryAdd, so a host — or a test pinning a run to an older version — can serve a catalogue of its own.
        services.TryAddSingleton<IWorkflowCatalogue, ShippedWorkflowCatalogue>();
        services.AddScoped<IValidator<StartWorkflowRunViewModel>, StartWorkflowRunViewModelValidator>();
        services.AddScoped<IValidator<SaveWorkflowStepDraftViewModel>, SaveWorkflowStepDraftViewModelValidator>();
        services.AddScoped<IValidator<CompleteWorkflowStepViewModel>, CompleteWorkflowStepViewModelValidator>();
        services.AddScoped<IValidator<WorkflowStepBackViewModel>, WorkflowStepBackViewModelValidator>();
        services.AddScoped<IValidator<SkipWorkflowStepViewModel>, SkipWorkflowStepViewModelValidator>();
        services.AddScoped<IValidator<RestartWorkflowRunViewModel>, RestartWorkflowRunViewModelValidator>();
        services.AddScoped<IWorkflowRunRepository, WorkflowRunRepository>();
        services.AddScoped<IWorkflowRunDataLayer, WorkflowRunDataLayer>();
        services.AddScoped<IWorkflowFacts, WorkflowFacts>();
        services.AddScoped<IWorkflowRunBusiness, WorkflowRunBusiness>();
        services.AddScoped<IWorkflowRunFacade, WorkflowRunFacade>();

        return services;
    }
}
