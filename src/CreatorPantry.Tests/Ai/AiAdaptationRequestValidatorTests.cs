using CreatorPantry.Domain.Modules.Ai.Managers;
using FluentValidation;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// <see cref="RequestRecipeAdaptationViewModelValidator"/>: the one-goal, one-target-shape rules FluentValidation
/// enforces before anything reaches Business.
/// </summary>
public sealed class AiAdaptationRequestValidatorTests
{
    private static readonly RequestRecipeAdaptationViewModelValidator Validator = new();
    private static readonly Guid VersionId = Guid.NewGuid();

    [Fact]
    public void An_unspecified_goal_is_invalid()
    {
        var result = Validator.Validate(new RequestRecipeAdaptationViewModel { SourceVersionId = VersionId });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void No_source_version_is_invalid()
    {
        var result = Validator.Validate(new RequestRecipeAdaptationViewModel
        {
            Goal = AiAdaptationGoal.Dietary,
            GoalDetail = "gluten-free",
        });

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData(AiAdaptationGoal.Dietary)]
    [InlineData(AiAdaptationGoal.Equipment)]
    [InlineData(AiAdaptationGoal.SkillLevel)]
    public void A_missing_goal_detail_is_invalid_for_every_goal_but_yield(AiAdaptationGoal goal)
    {
        var result = Validator.Validate(new RequestRecipeAdaptationViewModel
        {
            SourceVersionId = VersionId,
            Goal = goal,
        });

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData(AiAdaptationGoal.Dietary)]
    [InlineData(AiAdaptationGoal.Equipment)]
    [InlineData(AiAdaptationGoal.SkillLevel)]
    public void A_declared_detail_is_valid_for_every_goal_but_yield(AiAdaptationGoal goal)
    {
        var result = Validator.Validate(new RequestRecipeAdaptationViewModel
        {
            SourceVersionId = VersionId,
            Goal = goal,
            GoalDetail = "what the goal means here",
        });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void A_yield_goal_needs_no_detail()
    {
        var result = Validator.Validate(new RequestRecipeAdaptationViewModel
        {
            SourceVersionId = VersionId,
            Goal = AiAdaptationGoal.Yield,
            TargetMultiplier = 2m,
        });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void A_yield_goal_with_neither_target_is_invalid()
    {
        var result = Validator.Validate(new RequestRecipeAdaptationViewModel
        {
            SourceVersionId = VersionId,
            Goal = AiAdaptationGoal.Yield,
        });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void A_yield_goal_with_both_targets_is_invalid()
    {
        var result = Validator.Validate(new RequestRecipeAdaptationViewModel
        {
            SourceVersionId = VersionId,
            Goal = AiAdaptationGoal.Yield,
            TargetMultiplier = 2m,
            TargetYieldQuantity = 24m,
        });

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_nonpositive_multiplier_is_invalid(decimal multiplier)
    {
        var result = Validator.Validate(new RequestRecipeAdaptationViewModel
        {
            SourceVersionId = VersionId,
            Goal = AiAdaptationGoal.Yield,
            TargetMultiplier = multiplier,
        });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void A_multiplier_on_a_non_yield_goal_is_invalid()
    {
        var result = Validator.Validate(new RequestRecipeAdaptationViewModel
        {
            SourceVersionId = VersionId,
            Goal = AiAdaptationGoal.Dietary,
            GoalDetail = "gluten-free",
            TargetMultiplier = 2m,
        });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void A_target_yield_on_a_non_yield_goal_is_invalid()
    {
        var result = Validator.Validate(new RequestRecipeAdaptationViewModel
        {
            SourceVersionId = VersionId,
            Goal = AiAdaptationGoal.SkillLevel,
            GoalDetail = "a nervous first-timer",
            TargetYieldQuantity = 24m,
        });

        Assert.False(result.IsValid);
    }
}
