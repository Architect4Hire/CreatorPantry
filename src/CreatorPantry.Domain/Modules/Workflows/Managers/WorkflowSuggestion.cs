namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>Something to do once a workflow is complete: another workflow, or a capability on its own.</summary>
/// <remarks>
/// The suggestion's label is its target's own <see cref="WorkflowDefinition.Goal"/> or
/// <see cref="WorkflowCapability.Name"/>, so it cannot drift from the thing it opens. Only <c>Why</c> is
/// written here.
/// </remarks>
public abstract record WorkflowSuggestion
{
    private WorkflowSuggestion()
    {
    }

    /// <summary>Why this is a good next thing, in one ordinary sentence.</summary>
    public abstract string Why { get; }

    public sealed record Workflow(WorkflowKey Key, string Why) : WorkflowSuggestion
    {
        public override string Why { get; } = Why;
    }

    public sealed record Capability(WorkflowCapabilityKey Key, string Why) : WorkflowSuggestion
    {
        public override string Why { get; } = Why;
    }
}
