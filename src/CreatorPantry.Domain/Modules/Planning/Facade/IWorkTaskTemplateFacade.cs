using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Planning.Business;
using CreatorPantry.Domain.Modules.Planning.Managers;

namespace CreatorPantry.Domain.Modules.Planning.Facade;

/// <summary>The application boundary for routines (13A.30): making one routine's occurrences, for the worker.</summary>
public interface IWorkTaskTemplateFacade
{
    /// <summary>
    /// Makes one routine's due occurrences in the resolved workspace, for the pass holding its lease. The worker
    /// resolves the workspace first; the routine is read through the workspace filter, never by id alone.
    /// </summary>
    Task<WorkTaskTemplateRun> MaterializeAsync(Guid templateId, Guid leaseToken, CancellationToken cancellationToken);
}

internal sealed class WorkTaskTemplateFacade(IWorkTaskTemplateBusiness business, IWorkspaceContext workspace) : IWorkTaskTemplateFacade
{
    public Task<WorkTaskTemplateRun> MaterializeAsync(Guid templateId, Guid leaseToken, CancellationToken cancellationToken)
    {
        // Only ever after the worker has resolved the routine's workspace: without it the query filter would have
        // nothing to scope the read by.
        if (!workspace.IsResolved)
        {
            throw new InvalidOperationException("A routine's occurrences are made only inside its resolved workspace.");
        }

        return business.MaterializeAsync(templateId, leaseToken, cancellationToken);
    }
}
