using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Ai.Data;

/// <summary>
/// The bounded reads the dashboard makes of AI requests (13.11): proposals awaiting a decision. Served by
/// <c>IX_AiOperations_Workspace_Status_StatusChanged</c>, which leads on the workspace like every list here.
/// </summary>
public interface IAiDashboardRepository
{
    Task<int> CountAwaitingDecisionAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<AwaitingAiProposalServiceModel>> ListAwaitingDecisionAsync(int limit, CancellationToken cancellationToken);
}

internal sealed class AiDashboardRepository(CreatorPantryDbContext context) : IAiDashboardRepository
{
    public Task<int> CountAwaitingDecisionAsync(CancellationToken cancellationToken) =>
        context.AiOperations.AsNoTracking().CountAsync(operation => operation.Status == AiOperationStatus.Proposed, cancellationToken);

    public async Task<IReadOnlyList<AwaitingAiProposalServiceModel>> ListAwaitingDecisionAsync(
        int limit, CancellationToken cancellationToken) =>
        await context.AiOperations.AsNoTracking()
            .Where(operation => operation.Status == AiOperationStatus.Proposed)
            .OrderByDescending(operation => operation.StatusChangedAt)
            .ThenByDescending(operation => operation.Id)
            .Take(limit)

            // Ids, a kind and a time. Never the request's inputs or the proposal's words.
            .Select(operation => new AwaitingAiProposalServiceModel(
                operation.Id, operation.TaskType, operation.RecipeId, operation.StatusChangedAt))
            .ToListAsync(cancellationToken);
}

public interface IAiDashboardDataLayer
{
    Task<AiProposalSummaryServiceModel> GetAwaitingDecisionAsync(int limit, CancellationToken cancellationToken);
}

internal sealed class AiDashboardDataLayer(IAiDashboardRepository repository) : IAiDashboardDataLayer
{
    public async Task<AiProposalSummaryServiceModel> GetAwaitingDecisionAsync(int limit, CancellationToken cancellationToken) =>
        new(
            await repository.CountAwaitingDecisionAsync(cancellationToken),
            await repository.ListAwaitingDecisionAsync(limit, cancellationToken));
}
