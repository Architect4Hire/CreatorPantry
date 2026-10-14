namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>Something that must be true before a workflow, or one of its steps, can start.</summary>
/// <remarks>
/// A closed set: the constructor is private, so the members nested here are the only prerequisites there are.
/// Adding one is a contract change that the evaluator (13A.5, 13A.8) has to learn in the same change.
/// </remarks>
public abstract record WorkflowPrerequisite
{
    private WorkflowPrerequisite()
    {
    }

    /// <summary>An earlier step of the same definition is complete. Only valid on a step.</summary>
    public sealed record StepCompleted(WorkflowStepKey Step) : WorkflowPrerequisite;

    /// <summary>The run holds a link to a resource of this kind that the caller can read in the workspace.</summary>
    public sealed record ResourceLinked(WorkflowResourceKind Kind) : WorkflowPrerequisite;

    /// <summary>Something about the workspace itself is true, as the module that owns it says.</summary>
    public sealed record WorkspaceFact(WorkflowWorkspaceFact Fact) : WorkflowPrerequisite;
}

/// <summary>A workspace-level fact a step can wait for. Each is decided by the module that owns it.</summary>
public enum WorkflowWorkspaceFact
{
    /// <summary>
    /// The workspace has a scheduling time zone (<c>BrandProfile.TimeZoneId</c>). Scheduling refuses to guess one,
    /// so a step that schedules waits for it and says where to set it.
    /// </summary>
    SchedulingZoneSet = 1,
}
