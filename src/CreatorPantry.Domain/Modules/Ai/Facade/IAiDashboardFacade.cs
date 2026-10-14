using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Facade;

/// <summary>What the dashboard asks the AI module (13.11): the proposals waiting for the creator.</summary>
/// <remarks>Read by every member; the workspace query filter scopes it. Not cached.</remarks>
public interface IAiDashboardFacade
{
    /// <inheritdoc cref="IAiDashboardBusiness.GetAwaitingDecisionAsync"/>
    Task<AiProposalSummaryServiceModel> GetAwaitingDecisionAsync(int limit, CancellationToken cancellationToken);
}

internal sealed class AiDashboardFacade(IAiDashboardBusiness business) : IAiDashboardFacade
{
    public Task<AiProposalSummaryServiceModel> GetAwaitingDecisionAsync(int limit, CancellationToken cancellationToken) =>
        business.GetAwaitingDecisionAsync(limit, cancellationToken);
}
