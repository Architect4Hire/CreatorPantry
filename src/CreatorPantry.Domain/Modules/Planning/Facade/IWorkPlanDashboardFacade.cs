using CreatorPantry.Domain.Modules.Planning.Business;
using CreatorPantry.Domain.Modules.Planning.Managers;

namespace CreatorPantry.Domain.Modules.Planning.Facade;

/// <summary>
/// What the dashboard asks the planning module (13A.33a): the caller's own plan for a day the caller has already
/// decided, bounded and read-only.
/// </summary>
/// <remarks>
/// No role bar and no cache, for the daily view's reasons (<see cref="IWorkTaskFacade.GetTodayAsync"/>): the
/// answer is the caller's own work, and it changes with every tick of a task and turns over at midnight.
/// </remarks>
public interface IWorkPlanDashboardFacade
{
    /// <inheritdoc cref="IWorkPlanDashboardBusiness.GetAsync"/>
    Task<WorkPlanSummaryServiceModel> GetAsync(DateOnly today, string timeZoneId, CancellationToken cancellationToken);
}

internal sealed class WorkPlanDashboardFacade(IWorkPlanDashboardBusiness business) : IWorkPlanDashboardFacade
{
    public Task<WorkPlanSummaryServiceModel> GetAsync(
        DateOnly today, string timeZoneId, CancellationToken cancellationToken) =>
        business.GetAsync(today, timeZoneId, cancellationToken);
}
