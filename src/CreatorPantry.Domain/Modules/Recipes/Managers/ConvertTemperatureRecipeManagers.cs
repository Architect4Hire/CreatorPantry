using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// A temperature-conversion preview request: which recipe version it is asked in the context of, an already
/// structured temperature value, the scale to convert it from and to, and the display/context fields
/// <c>TemperatureConversionCalculator</c> passes through untouched (CALC-003).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The source version is required</strong>, for the reason <see cref="ScaleRecipeViewModel.SourceVersionNumber"/>
/// is — see <see cref="ConvertUnitsViewModel"/>'s remarks, which apply identically here.
/// </para>
/// <para>
/// <strong><see cref="Value"/> must already be a structured number.</strong> recipes.md forbids inferring a
/// temperature from vague heat language ("medium-high") — there is no field here that could carry that, and
/// none is read as one.
/// </para>
/// <para>Read-only: nothing here is persisted, and nothing about the recipe changes because a temperature was converted.</para>
/// </remarks>
public sealed record ConvertTemperatureViewModel(
    int SourceVersionNumber,
    decimal Value,
    TemperatureScale FromScale,
    TemperatureScale ToScale,
    int Precision,
    string? OvenModeContext,
    string? SafetyNote);

/// <summary>Shape validation for <see cref="ConvertTemperatureViewModel"/>.</summary>
/// <remarks>
/// This calculator never fails once its inputs are well formed — it is a total function over any decimal and
/// any two of exactly two scales — so this validator carries everything the route ever refuses.
/// </remarks>
public sealed class ConvertTemperatureViewModelValidator : AbstractValidator<ConvertTemperatureViewModel>
{
    public ConvertTemperatureViewModelValidator()
    {
        RuleFor(model => model.SourceVersionNumber)
            .GreaterThanOrEqualTo(RecipePolicy.FirstVersionNumber)
                .WithMessage($"A version number starts at {RecipePolicy.FirstVersionNumber}.")
            .OverridePropertyName(nameof(ConvertTemperatureViewModel.SourceVersionNumber));

        RuleFor(model => model.FromScale)
            .IsInEnum()
                .WithMessage("That temperature scale is not recognized.")
            .OverridePropertyName(nameof(ConvertTemperatureViewModel.FromScale));

        RuleFor(model => model.ToScale)
            .IsInEnum()
                .WithMessage("That temperature scale is not recognized.")
            .OverridePropertyName(nameof(ConvertTemperatureViewModel.ToScale));

        // Bounded above, not only below: Quantity.ToDecimal refuses a scale outside 0–28 by throwing, so an
        // unbounded precision was a 500 a creator could reach by typing. The cap is the platform's own
        // documented display maximum, already enforced on units and density references.
        RuleFor(model => model.Precision)
            .InclusiveBetween(QuantityFormat.MinDisplayPrecision, QuantityFormat.MaxDisplayPrecision)
                .WithMessage(
                    $"Precision must be between {QuantityFormat.MinDisplayPrecision} and {QuantityFormat.MaxDisplayPrecision}.")
            .OverridePropertyName(nameof(ConvertTemperatureViewModel.Precision));
    }
}

/// <summary>
/// A computed temperature-conversion preview for one explicit recipe version — a proposal only; nothing here
/// is persisted or changes the recipe (recipes.md).
/// </summary>
/// <param name="SourceVersionNumber">The version the preview was computed in the context of, echoed back as provenance.</param>
/// <param name="Result">
/// The conversion itself, computed by <c>TemperatureConversionCalculator</c> (7.8). Reused as-is rather than
/// re-shaped — it already echoes the oven-mode context and safety note untouched.
/// </param>
public sealed record RecipeTemperatureConversionResultServiceModel(int SourceVersionNumber, TemperatureConversionResult Result);
