using CreatorPantry.Domain.Modules.Workflows.Business;
using CreatorPantry.Domain.Modules.Workflows.Managers;

namespace CreatorPantry.Domain.Modules.Workflows.Facade;

/// <summary>What the dashboard asks the workflow module (13A.33a): what to carry on with, and what has stopped.</summary>
/// <remarks>
/// Read by every member, a Viewer included, as the run list is: the workspace route policy and the resolved
/// context are the authorization, the query filter scopes it, and what the caller's role changes is only whether
/// a step is theirs to take. Nothing to validate — there is no input. Not cached, for the dashboard's reasons.
/// </remarks>
public interface IWorkflowDashboardFacade
{
    /// <inheritdoc cref="IWorkflowDashboardBusiness.GetSummaryAsync"/>
    Task<WorkflowDashboardSummaryServiceModel> GetSummaryAsync(CancellationToken cancellationToken);
}

internal sealed class WorkflowDashboardFacade(IWorkflowDashboardBusiness business) : IWorkflowDashboardFacade
{
    public Task<WorkflowDashboardSummaryServiceModel> GetSummaryAsync(CancellationToken cancellationToken) =>
        business.GetSummaryAsync(cancellationToken);
}
