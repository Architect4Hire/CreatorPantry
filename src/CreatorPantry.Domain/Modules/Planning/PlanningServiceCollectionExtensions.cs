using CreatorPantry.Domain.Modules.Planning.Business;
using CreatorPantry.Domain.Modules.Planning.Data;
using CreatorPantry.Domain.Modules.Planning.Facade;
using CreatorPantry.Domain.Modules.Planning.Managers;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        services.TryAddScoped<IWorkTaskRepository, WorkTaskRepository>();
        services.AddScoped<IWorkTaskDataLayer, WorkTaskDataLayer>();
        services.AddScoped<IWorkTaskBusiness, WorkTaskBusiness>();
        services.AddScoped<IWorkTaskFacade, WorkTaskFacade>();
        services.AddScoped<IValidator<CreateWorkTaskViewModel>, CreateWorkTaskViewModelValidator>();

        services.AddScoped<IWorkTaskWeekRepository, WorkTaskWeekRepository>();
        services.AddScoped<IWorkTaskWeekDataLayer, WorkTaskWeekDataLayer>();
        services.AddScoped<IWorkTaskWeekBusiness, WorkTaskWeekBusiness>();
        services.AddScoped<IValidator<WeekWorkTasksViewModel>, WeekWorkTasksViewModelValidator>();

        services.AddScoped<WorkTaskLinkChecks>();
        services.AddScoped<IWorkTaskEditBusiness, WorkTaskEditBusiness>();
        services.AddScoped<IValidator<UpdateWorkTaskViewModel>, UpdateWorkTaskViewModelValidator>();

        services.AddScoped<IWorkTaskMoveBusiness, WorkTaskMoveBusiness>();
        services.AddScoped<IValidator<MoveWorkTaskViewModel>, MoveWorkTaskViewModelValidator>();

        services.AddScoped<IWorkTaskReorderBusiness, WorkTaskReorderBusiness>();
        services.AddScoped<IValidator<ReorderWorkTaskViewModel>, ReorderWorkTaskViewModelValidator>();

        services.AddScoped<IWorkTaskCompletionBusiness, WorkTaskCompletionBusiness>();
        services.AddScoped<IValidator<WorkTaskCompletionViewModel>, WorkTaskCompletionViewModelValidator>();

        services.AddScoped<IWorkTaskFocusRepository, WorkTaskFocusRepository>();
        services.AddScoped<IWorkTaskFocusDataLayer, WorkTaskFocusDataLayer>();
        services.AddScoped<IWorkTaskFocusBusiness, WorkTaskFocusBusiness>();
        services.AddScoped<IValidator<SetWorkTaskTopThreeViewModel>, SetWorkTaskTopThreeViewModelValidator>();

        services.AddScoped<IWorkTaskFromStepBusiness, WorkTaskFromStepBusiness>();
        services.AddScoped<IValidator<WorkTaskStepSuggestionViewModel>, WorkTaskStepSuggestionViewModelValidator>();
        services.AddScoped<IValidator<AddWorkTaskFromStepViewModel>, AddWorkTaskFromStepViewModelValidator>();

        return services;
    }

    /// <summary>
    /// Routines and the worker that makes their occurrences (13A.30), for the host that runs background work.
    /// Requires <see cref="CreatorPantry.Domain.Managers.Persistence.CreatorPantryDbContext"/>, tenancy and
    /// application time — and nothing from the modules a task links to, because an occurrence links to none, so the
    /// worker does not take on the task routes' dependencies to make one.
    /// </summary>
    public static IServiceCollection AddPlanningRecurrence(this IServiceCollection services)
    {
        services.TryAddScoped<IWorkTaskRepository, WorkTaskRepository>();
        services.AddScoped<IWorkTaskTemplateDataLayer, WorkTaskTemplateDataLayer>();
        services.AddScoped<IWorkTaskTemplateBusiness, WorkTaskTemplateBusiness>();
        services.AddScoped<IWorkTaskTemplateFacade, WorkTaskTemplateFacade>();
        services.AddScoped<WorkTaskTemplateClaimRepository>();
        services.AddScoped<IWorkTaskTemplateWorker, WorkTaskTemplateWorker>();

        return services;
    }
}
