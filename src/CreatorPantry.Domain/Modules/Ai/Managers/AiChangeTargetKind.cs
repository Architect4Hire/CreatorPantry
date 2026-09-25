namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Which part of the recipe a proposed change addresses.
/// </summary>
/// <remarks>
/// An enum rather than a path string, so a change can be checked against its operation's
/// <see cref="AiOperationScope"/> before anything is applied. A free-text path would make that check string
/// matching, and an unparseable path arriving from a model is exactly the unvalidated instruction a proposal
/// must never carry.
/// </remarks>
public enum AiChangeTargetKind
{
    /// <summary>Not declared. Never valid on a stored row; the check constraint refuses it.</summary>
    Unspecified = 0,

    /// <summary>The recipe's own fields — title, headnote, times, yield. Has no child id.</summary>
    Recipe = 1,

    Ingredient = 2,

    IngredientGroup = 3,

    InstructionStep = 4,

    InstructionGroup = 5,

    Equipment = 6,

    AssetLink = 7,

    Tag = 8,
}
