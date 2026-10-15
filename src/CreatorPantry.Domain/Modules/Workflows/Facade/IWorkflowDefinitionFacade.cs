using CreatorPantry.Domain.Modules.Workflows.Business;
using CreatorPantry.Domain.Modules.Workflows.Managers;

namespace CreatorPantry.Domain.Modules.Workflows.Facade;

/// <summary>The workflows a workspace offers, as the hub shows them (13A.13).</summary>
public interface IWorkflowDefinitionFacade
{
    /// <summary>Every workflow's newest version, and whether the reader can start it now. Any member may read it.</summary>
    Task<IReadOnlyList<WorkflowDefinitionSummaryServiceModel>> ListAsync(CancellationToken cancellationToken);
}

internal sealed class WorkflowDefinitionFacade(IWorkflowDefinitionBusiness business) : IWorkflowDefinitionFacade
{
    public Task<IReadOnlyList<WorkflowDefinitionSummaryServiceModel>> ListAsync(CancellationToken cancellationToken) =>
        business.ListAsync(cancellationToken);
}
