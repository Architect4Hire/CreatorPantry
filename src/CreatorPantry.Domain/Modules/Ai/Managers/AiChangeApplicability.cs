namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Which <see cref="AiChangeKind"/> and <see cref="AiChangeTargetKind"/> pairs a proposal may offer, because
/// they are the pairs the recipe update seam can actually apply.
/// </summary>
/// <remarks>
/// <para>
/// The companion to <see cref="AiDiffFields"/>, and it exists for the same reason: <strong>do not offer a
/// change that cannot be accepted.</strong> A creator reading a diff, choosing a change, confirming it and
/// then being told the system has no way to apply it is a worse outcome than never having been offered it.
/// So the gate sits here, before the proposal is stored, rather than at acceptance.
/// </para>
/// <para>
/// <strong><see cref="AiChangeKind.Add"/> is permitted for one target only, and the reason is structural.</strong>
/// <c>AiStructuredChange</c> carries a single <c>AfterValue</c>, so an addition can express a whole child only
/// where the child <em>is</em> one string — which is true of a tag and of nothing else. An added instruction
/// step has text, a note, a duration and a temperature, and a row with one value cannot say what they are. A
/// proposal may therefore not add a step until a change can carry a child payload.
/// </para>
/// <para>
/// <strong>Equipment and asset links are absent throughout</strong>, matching <see cref="AiDiffFields"/>: the
/// recipe patch contract has no field for either, so no change to one has a path through ordinary recipe
/// validation, and they remain genuinely unexpressible.
/// </para>
/// <para>
/// <strong>Ingredients were absent for that reason and no longer are.</strong>
/// <c>UpdateRecipeViewModel.IngredientGroups</c> closed the gap on the recipe side, and
/// <c>RecipeProposalApplication</c> now carries an ingredient draft list beside the instruction one — so an
/// accepted ingredient change travels the same merge path a hand-typed one does. Without those entries
/// <c>AiOperationScope.Ingredients</c> was a scope in which every proposed change would be refused.
/// </para>
/// </remarks>
public static class AiChangeApplicability
{
    /// <remarks>
    /// Read as a table: a target kind maps to the kinds of change that can reach it.
    /// <see cref="AiChangeTargetKind.Recipe"/> takes only <see cref="AiChangeKind.Set"/> because a recipe is
    /// not a child of anything — there is nothing to add it to, remove it from or move it within.
    /// </remarks>
    private static readonly Dictionary<AiChangeTargetKind, AiChangeKind[]> Applicable = new()
    {
        [AiChangeTargetKind.Recipe] = [AiChangeKind.Set],
        [AiChangeTargetKind.InstructionGroup] = [AiChangeKind.Set, AiChangeKind.Remove, AiChangeKind.Move],
        [AiChangeTargetKind.InstructionStep] = [AiChangeKind.Set, AiChangeKind.Remove, AiChangeKind.Move],

        // Add is absent here for the reason the remarks give, not because ingredients are special: a single
        // AfterValue cannot carry a whole line's quantity, unit, name and note.
        [AiChangeTargetKind.IngredientGroup] = [AiChangeKind.Set, AiChangeKind.Remove, AiChangeKind.Move],
        [AiChangeTargetKind.Ingredient] = [AiChangeKind.Set, AiChangeKind.Remove, AiChangeKind.Move],

        // A tag is a name. Adding one is submitting that name, removing one is dropping it, and there is no
        // field on it to set and no order to move it within — a recipe's tags are a set.
        [AiChangeTargetKind.Tag] = [AiChangeKind.Add, AiChangeKind.Remove],
    };

    /// <summary>Whether a proposal may offer this change at all.</summary>
    public static bool IsApplicable(AiChangeKind kind, AiChangeTargetKind target) =>
        Applicable.TryGetValue(target, out var kinds) && Array.IndexOf(kinds, kind) >= 0;

    /// <summary>The kinds that can reach one target; empty for a target no change may address.</summary>
    public static IReadOnlyList<AiChangeKind> For(AiChangeTargetKind target) =>
        Applicable.TryGetValue(target, out var kinds) ? kinds : [];

    /// <summary>Every target a proposal may address, for a message that explains a refusal.</summary>
    public static IReadOnlyCollection<AiChangeTargetKind> Targets => Applicable.Keys;
}
