using CreatorPantry.Domain.Modules.Dashboard.Business;
using CreatorPantry.Domain.Modules.Dashboard.Managers;

namespace CreatorPantry.Domain.Modules.Dashboard.Facade;

/// <summary>The workspace dashboard (13.11): what controllers and, later, other surfaces call to read it.</summary>
/// <remarks>
/// <para>
/// Every member may read, a Viewer included: the workspace route policy and the resolved context are the
/// authorization, and each module's own query filter scopes what it reports. There is no input to validate.
/// </para>
/// <para>
/// <strong>Not cached.</strong> It is workspace-private, changes with every edit anywhere in the workspace, and
/// costs a fixed number of indexed statements; a cache would be one more thing to invalidate correctly in six
/// modules for no measured gain.
/// </para>
/// </remarks>
public interface IDashboardFacade
{
    /// <inheritdoc cref="IDashboardBusiness.GetAsync"/>
    Task<DashboardServiceModel> GetAsync(CancellationToken cancellationToken);
}

internal sealed class DashboardFacade(IDashboardBusiness business) : IDashboardFacade
{
    public Task<DashboardServiceModel> GetAsync(CancellationToken cancellationToken) => business.GetAsync(cancellationToken);
}
