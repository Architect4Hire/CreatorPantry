using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// A recipe scaling request: which version to scale, and by how much (ING-003, CALC-001/002/005/006).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The source version is required, not optional.</strong> Unlike a duplicate, which defaults to a
/// recipe's current content when none is named, a calculation reasons about the exact content it was shown —
/// never about whatever happens to be live by the time the request arrives.
/// </para>
/// <para>Read-only: nothing here is persisted, and nothing about the recipe changes because it was scaled.</para>
/// </remarks>
public sealed record ScaleRecipeViewModel(int SourceVersionNumber, decimal? Multiplier, decimal? TargetYieldQuantity);

/// <summary>
/// Shape validation for <see cref="ScaleRecipeViewModel"/>: a version number is at least 1, and exactly one of
/// <see cref="ScaleRecipeViewModel.Multiplier"/> or <see cref="ScaleRecipeViewModel.TargetYieldQuantity"/> is
/// named.
/// </summary>
/// <remarks>
/// Positivity and target-yield feasibility (the recipe's own yield must be a structured number to scale
/// toward) are checked again in <c>RecipeScalingCalculator</c> itself — this validator only rules out shapes a
/// lookup would never need to run for.
/// </remarks>
public sealed class ScaleRecipeViewModelValidator : AbstractValidator<ScaleRecipeViewModel>
{
    public ScaleRecipeViewModelValidator()
    {
        RuleFor(model => model.SourceVersionNumber)
            .GreaterThanOrEqualTo(RecipePolicy.FirstVersionNumber)
                .WithMessage($"A version number starts at {RecipePolicy.FirstVersionNumber}.")
            .OverridePropertyName(nameof(ScaleRecipeViewModel.SourceVersionNumber));

        RuleFor(model => model)
            .Must(model => (model.Multiplier is not null) ^ (model.TargetYieldQuantity is not null))
                .WithMessage("Name exactly one of multiplier or targetYieldQuantity.")
            .OverridePropertyName(nameof(ScaleRecipeViewModel.Multiplier));

        RuleFor(model => model.Multiplier)
            .GreaterThan(0m)
                .WithMessage("The multiplier must be greater than zero.")
            .When(model => model.Multiplier is not null)
            .OverridePropertyName(nameof(ScaleRecipeViewModel.Multiplier));

        RuleFor(model => model.TargetYieldQuantity)
            .GreaterThan(0m)
                .WithMessage("The target yield must be greater than zero.")
            .When(model => model.TargetYieldQuantity is not null)
            .OverridePropertyName(nameof(ScaleRecipeViewModel.TargetYieldQuantity));
    }
}

/// <summary>
/// A computed scaling preview for one explicit recipe version — a proposal only; nothing here is persisted or
/// changes the recipe (recipes.md).
/// </summary>
/// <param name="SourceVersionNumber">The version the preview was computed from, echoed back as provenance.</param>
/// <param name="Preview">
/// The calculation itself — factor, per-line results and warnings — computed by
/// <c>RecipeScalingCalculator</c> (7.6). Reused as-is rather than re-shaped: it already carries everything
/// ING-003 asks a scaling response to return.
/// </param>
public sealed record RecipeScalingResultServiceModel(int SourceVersionNumber, RecipeScalingPreview Preview);
