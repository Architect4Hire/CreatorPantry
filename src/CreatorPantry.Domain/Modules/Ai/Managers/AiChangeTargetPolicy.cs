using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Which <see cref="AiChangeTargetKind"/> the recipe module's own vocabulary can name, and as what.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The other half of <see cref="AiChangeApplicability"/>, and it lives beside it for that
/// reason.</strong> That table decides what a proposal may offer; this one decides what acceptance can
/// actually say to the recipe module. They describe the same boundary from two sides, and a target that
/// appears in one and not the other is a bug in whichever direction it points: a target applicable but
/// inexpressible is offered to a creator and refused when they accept it, and a target expressible but
/// inapplicable is a route into the recipe that the proposal gate never checked.
/// </para>
/// <para>
/// They drifted once, in that first direction — ingredients became applicable without becoming expressible,
/// so an ingredient-scoped revision could be asked for, waited for, reviewed, and then refused at the moment
/// of acceptance. Pulling the mapping out of the private method it was hiding in is what lets a test assert
/// the two agree instead of a creator discovering they do not.
/// </para>
/// <para>
/// <see cref="ProposedRecipeTarget"/> is one of the purpose-built input types the recipe module owns for
/// exactly this traffic, so naming it here is the documented way across the boundary rather than an exception
/// to it (backend.md).
/// </para>
/// </remarks>
public static class AiChangeTargetPolicy
{
    /// <summary>
    /// What the recipe module calls this target, or <c>null</c> when its vocabulary cannot express it.
    /// </summary>
    /// <remarks>
    /// <see cref="AiChangeTargetKind.RecipeConcept"/> and
    /// <see cref="AiChangeTargetKind.IngredientSubstitution"/> answer <c>null</c> deliberately and permanently.
    /// Neither describes a change to a recipe — one pitches a recipe that does not exist yet, the other gives
    /// advice about an ingredient — and both are absent from <see cref="AiChangeApplicability"/> to match. For
    /// AIREC-004 that absence is the guarantee: with no expression there is no code path from a stored
    /// substitution row to a recipe edit, which is what "no automatic replacement" has to mean.
    /// </remarks>
    public static ProposedRecipeTarget? For(AiChangeTargetKind target) => target switch
    {
        AiChangeTargetKind.Recipe => ProposedRecipeTarget.Recipe,
        AiChangeTargetKind.InstructionGroup => ProposedRecipeTarget.InstructionGroup,
        AiChangeTargetKind.InstructionStep => ProposedRecipeTarget.InstructionStep,
        AiChangeTargetKind.IngredientGroup => ProposedRecipeTarget.IngredientGroup,
        AiChangeTargetKind.Ingredient => ProposedRecipeTarget.Ingredient,
        AiChangeTargetKind.Tag => ProposedRecipeTarget.Tag,
        _ => null,
    };
}
