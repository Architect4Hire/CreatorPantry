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

    /// <summary>
    /// A generated recipe concept — a pitch, not a canonical recipe. Only ever <see cref="AiChangeKind.Add"/>
    /// (the concept's title) followed by <see cref="AiChangeKind.Set"/> rows (its other fields), and never
    /// resolved against a pinned recipe snapshot: a concept-generation operation names no recipe at all, so
    /// there is nothing for <c>AiDiffCalculator</c> to diff against. <c>AiChangeApplicability</c> and
    /// <c>AiDiffFields</c> do not cover it for the same reason — neither is consulted for a target with no
    /// recipe to apply to.
    /// </summary>
    RecipeConcept = 9,

    /// <summary>
    /// One proposed ingredient substitution — advice about an ingredient, not a change to it. Only ever
    /// <see cref="AiChangeKind.Add"/> (the alternative's name) followed by <see cref="AiChangeKind.Set"/> rows
    /// carrying its guidance, the same flattening <see cref="RecipeConcept"/> uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Absent from <see cref="AiChangeApplicability"/> deliberately, and that absence is the
    /// guarantee.</strong> AIREC-004 must never replace an ingredient automatically, so there must be no code
    /// path from a stored substitution row to a recipe edit — and there is none, because the translation at
    /// the recipe-module boundary answers <c>null</c> for any target that enum does not cover. A creator acts
    /// on substitution advice by editing the recipe themselves, which is the point.
    /// </para>
    /// <para>
    /// Unlike <see cref="RecipeConcept"/>, an operation producing these rows <em>does</em> name a recipe and a
    /// pinned version: the advice depends on what the ingredient is doing in that method. What it does not do
    /// is address a change to either. See <see cref="AiOperationScope.Advisory"/>.
    /// </para>
    /// </remarks>
    IngredientSubstitution = 10,
}
