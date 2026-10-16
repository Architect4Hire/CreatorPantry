using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Planning.Business;
using CreatorPantry.Domain.Modules.Planning.Data;
using CreatorPantry.Domain.Modules.Planning.Facade;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Planning.Managers;

/// <summary>What one pass over due routines did.</summary>
public sealed record WorkTaskTemplatePassSummary(int Claimed, int Workspaces, int Made, int Ended, int NeedsAttention, int Skipped);

/// <summary>The worker's pass over routines due for their occurrences (13A.30).</summary>
public interface IWorkTaskTemplateWorker
{
    Task<WorkTaskTemplatePassSummary> RunPendingAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Claims due routines across workspaces — identifiers only — then, a workspace at a time, resolves it as the
/// service in a fresh scope and hands each routine to the facade.
/// </summary>
internal sealed class WorkTaskTemplateWorker(
    WorkTaskTemplateClaimRepository claims,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<WorkTaskTemplateWorker> logger) : IWorkTaskTemplateWorker
{
    public async Task<WorkTaskTemplatePassSummary> RunPendingAsync(CancellationToken cancellationToken)
    {
        var leaseToken = Guid.NewGuid();
        var claimed = await claims.ClaimDueAsync(leaseToken, clock.UtcNow, cancellationToken);

        if (claimed.Count is 0)
        {
            return new WorkTaskTemplatePassSummary(0, 0, 0, 0, 0, 0);
        }

        var byWorkspace = claimed.GroupBy(claim => claim.WorkspaceId).ToList();
        int made = 0, ended = 0, needsAttention = 0, skipped = 0;

        foreach (var workspace in byWorkspace)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await using var scope = scopeFactory.CreateAsyncScope();

            var resolution = await scope.ServiceProvider
                .GetRequiredService<IWorkspaceResolutionFacade>()
                .ResolveForServiceAsync(workspace.Key, cancellationToken);

            if (!resolution.Succeeded)
            {
                // The workspace is gone, and its routines with it. Nothing here may read them; the leases lapse.
                logger.LogWarning(
                    "Workspace {WorkspaceId} no longer resolves; {Count} routine claim(s) left to lapse.",
                    workspace.Key,
                    workspace.Count());
                skipped += workspace.Count();
                continue;
            }

            var facade = scope.ServiceProvider.GetRequiredService<IWorkTaskTemplateFacade>();

            foreach (var claim in workspace)
            {
                try
                {
                    var run = await facade.MaterializeAsync(claim.TemplateId, leaseToken, cancellationToken);
                    made += run.Made;

                    switch (run.Outcome)
                    {
                        case WorkTaskTemplateOutcome.Ended: ended++; break;
                        case WorkTaskTemplateOutcome.NeedsAttention: needsAttention++; break;
                        case WorkTaskTemplateOutcome.Skipped or WorkTaskTemplateOutcome.Stale: skipped++; break;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One routine's problem is not the pass's. Its lease lapses and the next pass tries again.
                    // The id is logged, never the routine's words.
                    logger.LogError(exception, "Making occurrences of routine {TemplateId} failed; its lease will lapse.", claim.TemplateId);
                    skipped++;
                }
            }
        }

        return new WorkTaskTemplatePassSummary(claimed.Count, byWorkspace.Count, made, ended, needsAttention, skipped);
    }
}
