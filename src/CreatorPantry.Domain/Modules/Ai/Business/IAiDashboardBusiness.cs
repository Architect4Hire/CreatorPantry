using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Business;

/// <summary>The AI module's part of the workspace dashboard (13.11). Read-only.</summary>
public interface IAiDashboardBusiness
{
    /// <summary>Proposals awaiting a decision, with at most <paramref name="limit"/> of the newest (1–10).</summary>
    Task<AiProposalSummaryServiceModel> GetAwaitingDecisionAsync(int limit, CancellationToken cancellationToken);
}

internal sealed class AiDashboardBusiness(IAiDashboardDataLayer dataLayer) : IAiDashboardBusiness
{
    public Task<AiProposalSummaryServiceModel> GetAwaitingDecisionAsync(int limit, CancellationToken cancellationToken) =>
        dataLayer.GetAwaitingDecisionAsync(Math.Clamp(limit, 1, 10), cancellationToken);
}
