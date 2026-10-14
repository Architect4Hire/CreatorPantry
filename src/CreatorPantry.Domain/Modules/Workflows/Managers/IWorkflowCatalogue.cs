namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>The workflow definitions a host serves: what a start picks from and what a read resolves against.</summary>
/// <remarks>
/// A service over <see cref="WorkflowCatalogue"/> rather than the static class itself, so that what a run reads
/// against can be stood in for — the one way to test a run pinned to a version older than the newest, while the
/// shipped catalogue has only ever had one.
/// </remarks>
public interface IWorkflowCatalogue
{
    IReadOnlyList<WorkflowDefinition> All { get; }

    /// <summary>The version a run pinned, or null when no such key and version was ever shipped.</summary>
    WorkflowDefinition? Find(WorkflowKey key, int version);

    /// <summary>The newest version of each workflow: what a new run starts on.</summary>
    IReadOnlyList<WorkflowDefinition> Latest();
}

/// <summary>The catalogue the application ships.</summary>
internal sealed class ShippedWorkflowCatalogue : IWorkflowCatalogue
{
    public IReadOnlyList<WorkflowDefinition> All => WorkflowCatalogue.All;

    public WorkflowDefinition? Find(WorkflowKey key, int version) => WorkflowCatalogue.Find(key, version);

    public IReadOnlyList<WorkflowDefinition> Latest() => WorkflowCatalogue.Latest();
}
