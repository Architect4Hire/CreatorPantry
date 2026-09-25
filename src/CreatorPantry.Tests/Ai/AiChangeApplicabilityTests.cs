using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The table that decides what a proposal may offer, asserted exhaustively rather than through whichever
/// combinations a seam happens to exercise.
/// </summary>
/// <remarks>
/// The rule this enforces is a product rule, not a technical one: never offer a creator a change that cannot be
/// accepted. Reviewing a suggestion, choosing it, confirming it and then being refused is worse than never
/// having seen it — so every pair is either applicable or refused before the proposal is stored.
/// </remarks>
public sealed class AiChangeApplicabilityTests
{
    /// <summary>
    /// Every pair, so a target or kind added later has to be classified deliberately. The expected set is
    /// written out here rather than derived from the table under test, which would assert nothing.
    /// </summary>
    private static readonly (AiChangeKind Kind, AiChangeTargetKind Target)[] Applicable =
    [
        (AiChangeKind.Set, AiChangeTargetKind.Recipe),
        (AiChangeKind.Set, AiChangeTargetKind.InstructionGroup),
        (AiChangeKind.Remove, AiChangeTargetKind.InstructionGroup),
        (AiChangeKind.Move, AiChangeTargetKind.InstructionGroup),
        (AiChangeKind.Set, AiChangeTargetKind.InstructionStep),
        (AiChangeKind.Remove, AiChangeTargetKind.InstructionStep),
        (AiChangeKind.Move, AiChangeTargetKind.InstructionStep),
        (AiChangeKind.Add, AiChangeTargetKind.Tag),
        (AiChangeKind.Remove, AiChangeTargetKind.Tag),
    ];

    [Fact]
    public void Exactly_these_pairs_are_applicable()
    {
        var actual = (
            from kind in Enum.GetValues<AiChangeKind>()
            from target in Enum.GetValues<AiChangeTargetKind>()
            where AiChangeApplicability.IsApplicable(kind, target)
            select (kind, target)).ToHashSet();

        Assert.Equal(Applicable.ToHashSet(), actual);
    }

    /// <summary>
    /// The unspecified members are never applicable, whichever side they appear on. A check constraint refuses
    /// them on a stored row too, but a row that got that far would already have been offered to a creator.
    /// </summary>
    [Fact]
    public void An_undeclared_kind_or_target_is_never_applicable()
    {
        Assert.All(
            Enum.GetValues<AiChangeTargetKind>(),
            target => Assert.False(AiChangeApplicability.IsApplicable(AiChangeKind.Unspecified, target)));

        Assert.All(
            Enum.GetValues<AiChangeKind>(),
            kind => Assert.False(AiChangeApplicability.IsApplicable(kind, AiChangeTargetKind.Unspecified)));
    }

    /// <summary>
    /// Ingredients are the gap this pair of prompts found, and it is a capability gap rather than a rule:
    /// <c>UpdateRecipeViewModel</c> has no ingredients field, so an accepted ingredient change has no path
    /// through ordinary recipe validation. Equipment and asset links are absent for the same reason.
    /// </summary>
    [Theory]
    [InlineData(AiChangeTargetKind.Ingredient)]
    [InlineData(AiChangeTargetKind.IngredientGroup)]
    [InlineData(AiChangeTargetKind.Equipment)]
    [InlineData(AiChangeTargetKind.AssetLink)]
    public void A_target_the_recipe_patch_cannot_express_takes_no_change(AiChangeTargetKind target)
    {
        Assert.Empty(AiChangeApplicability.For(target));
        Assert.DoesNotContain(target, AiChangeApplicability.Targets);
    }

    /// <summary>
    /// A tag is the one child an addition can name, because a change row carries one value and a tag's whole
    /// content is its name. A step is text, a note, a duration and a temperature, and one value cannot say what
    /// they are — so adding one is refused until a change can carry a child payload.
    /// </summary>
    [Fact]
    public void Only_a_tag_can_be_added()
    {
        var addable = Enum.GetValues<AiChangeTargetKind>()
            .Where(target => AiChangeApplicability.IsApplicable(AiChangeKind.Add, target))
            .ToArray();

        Assert.Equal([AiChangeTargetKind.Tag], addable);
    }

    /// <summary>A recipe is not a child of anything, so there is nothing to add, remove or move it within.</summary>
    [Fact]
    public void The_recipe_itself_only_takes_a_set()
    {
        Assert.Equal([AiChangeKind.Set], AiChangeApplicability.For(AiChangeTargetKind.Recipe));
    }

    /// <summary>
    /// The two tables have to agree: a target with settable fields must accept a <c>Set</c>, and one that accepts
    /// a <c>Set</c> must have a field to set. Either mismatch offers a change that goes nowhere.
    /// </summary>
    [Fact]
    public void Settable_fields_and_applicable_sets_agree()
    {
        foreach (var target in Enum.GetValues<AiChangeTargetKind>())
        {
            Assert.Equal(
                AiDiffFields.For(target).Count > 0,
                AiChangeApplicability.IsApplicable(AiChangeKind.Set, target));
        }
    }
}
