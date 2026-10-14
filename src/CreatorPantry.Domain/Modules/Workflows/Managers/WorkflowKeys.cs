namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>A workflow's stable identity, e.g. <c>recipe.create</c>. Never reused for a different journey.</summary>
/// <remarks>
/// A distinct type from <see cref="WorkflowStepKey"/> and <see cref="WorkflowCapabilityKey"/> so a step key cannot
/// be passed where a workflow is meant. The format is not checked here but by
/// <see cref="WorkflowDefinitionValidator"/>, which reports it as an error code beside every other defect rather
/// than throwing on the first.
/// </remarks>
public readonly record struct WorkflowKey(string Value)
{
    public static implicit operator WorkflowKey(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>A step's identity within one definition, e.g. <c>ingredients</c>. Stable across versions when the step is.</summary>
public readonly record struct WorkflowStepKey(string Value)
{
    public static implicit operator WorkflowStepKey(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>A capability's identity in <see cref="WorkflowCapabilities"/>, e.g. <c>recipes.edit</c>.</summary>
public readonly record struct WorkflowCapabilityKey(string Value)
{
    public static implicit operator WorkflowCapabilityKey(string value) => new(value);

    public override string ToString() => Value;
}
