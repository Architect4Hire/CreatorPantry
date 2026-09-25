using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// A yield-reconciliation preview request: which recipe version it is asked in the context of, and the
/// creator's explicit batch yield, serving count, serving size, and pan/vessel capacity — every one
/// independently optional (ING-005).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The source version is required</strong>, for the reason <see cref="ScaleRecipeViewModel.SourceVersionNumber"/>
/// is — see <see cref="ConvertUnitsViewModel"/>'s remarks. Business reads it for one further fact: the
/// dimension <see cref="BatchYield"/>, <see cref="ServingSize"/>, and <see cref="PanVolume"/> are expressed
/// in is resolved from that version's own stored yield unit, never invented.
/// </para>
/// <para>
/// <strong>Nothing here is derived from the recipe's own stored numbers.</strong> ING-005 asks for a preview
/// with "explicit inputs" — what the creator types into this request is everything the calculation reasons
/// about, never silently backed by <c>Recipe.YieldQuantity</c>.
/// </para>
/// <para>Read-only: nothing here is persisted, and nothing about the recipe changes because it was recalculated.</para>
/// </remarks>
public sealed record RecalculateYieldViewModel(
    int SourceVersionNumber,
    decimal? BatchYield,
    decimal? ServingCount,
    decimal? ServingSize,
    decimal? PanVolume,
    int? DisplayPrecision);

/// <summary>Shape validation for <see cref="RecalculateYieldViewModel"/>.</summary>
/// <remarks>
/// No rule requires any of the four values, or any minimum count of them — fewer than two is a legitimate
/// request whose answer is <c>YieldReconciliationStatus.InsufficientInput</c>, not a refusal (recipes.md: do
/// not invent a missing serving definition). Only positivity is checked here; everything else is
/// <c>YieldReconciliationCalculator</c>'s own case analysis.
/// </remarks>
public sealed class RecalculateYieldViewModelValidator : AbstractValidator<RecalculateYieldViewModel>
{
    public RecalculateYieldViewModelValidator()
    {
        RuleFor(model => model.SourceVersionNumber)
            .GreaterThanOrEqualTo(RecipePolicy.FirstVersionNumber)
                .WithMessage($"A version number starts at {RecipePolicy.FirstVersionNumber}.")
            .OverridePropertyName(nameof(RecalculateYieldViewModel.SourceVersionNumber));

        RuleFor(model => model.BatchYield)
            .GreaterThan(0m)
                .WithMessage("The batch yield must be greater than zero.")
            .When(model => model.BatchYield is not null)
            .OverridePropertyName(nameof(RecalculateYieldViewModel.BatchYield));

        RuleFor(model => model.ServingCount)
            .GreaterThan(0m)
                .WithMessage("The serving count must be greater than zero.")
            .When(model => model.ServingCount is not null)
            .OverridePropertyName(nameof(RecalculateYieldViewModel.ServingCount));

        RuleFor(model => model.ServingSize)
            .GreaterThan(0m)
                .WithMessage("The serving size must be greater than zero.")
            .When(model => model.ServingSize is not null)
            .OverridePropertyName(nameof(RecalculateYieldViewModel.ServingSize));

        RuleFor(model => model.PanVolume)
            .GreaterThan(0m)
                .WithMessage("The pan or vessel capacity must be greater than zero.")
            .When(model => model.PanVolume is not null)
            .OverridePropertyName(nameof(RecalculateYieldViewModel.PanVolume));

        RuleFor(model => model.DisplayPrecision)
            .GreaterThanOrEqualTo(0)
                .WithMessage("Precision cannot be negative.")
            .When(model => model.DisplayPrecision is not null)
            .OverridePropertyName(nameof(RecalculateYieldViewModel.DisplayPrecision));
    }
}

/// <summary>
/// A yield-reconciliation request as Business runs it: the four explicit values a
/// <see cref="RecalculateYieldViewModel"/> submitted, unchanged. Its own type only so Business is not handed
/// an HTTP-shaped record — <see cref="RecipeUnitConversionRequest"/> follows the same pattern.
/// </summary>
public sealed record RecipeYieldReconciliationRequest(
    decimal? BatchYield, decimal? ServingCount, decimal? ServingSize, decimal? PanVolume, int? DisplayPrecision);

/// <summary>
/// A computed yield-reconciliation preview for one explicit recipe version — a proposal only; nothing here is
/// persisted or changes the recipe (recipes.md).
/// </summary>
/// <param name="SourceVersionNumber">The version the preview was computed in the context of, echoed back as provenance.</param>
/// <param name="Preview">
/// The reconciliation itself, computed by <c>YieldReconciliationCalculator</c> (7.9). Reused as-is rather
/// than re-shaped.
/// </param>
public sealed record RecipeYieldReconciliationResultServiceModel(int SourceVersionNumber, YieldReconciliationPreview Preview);
