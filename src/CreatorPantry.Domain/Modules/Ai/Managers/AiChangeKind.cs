namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>What one proposed change does to the source.</summary>
public enum AiChangeKind
{
    /// <summary>Not declared. Never valid on a stored row; the check constraint refuses it.</summary>
    Unspecified = 0,

    /// <summary>Replace one field's value. The only kind that names a field.</summary>
    Set = 1,

    /// <summary>Add a new child — an ingredient line, a step, a piece of equipment.</summary>
    Add = 2,

    /// <summary>Remove an existing child.</summary>
    Remove = 3,

    /// <summary>
    /// Reorder an existing child. Separate from <see cref="Set"/> because position is not a field a model may
    /// assign freely: ordering is the recipe's method, and moving a step is a different act from rewording it.
    /// </summary>
    Move = 4,
}
