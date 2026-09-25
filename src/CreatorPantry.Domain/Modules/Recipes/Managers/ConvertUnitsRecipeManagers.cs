using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// A unit-conversion preview request: which recipe version it is asked in the context of, the quantity to
/// convert, and the unit to convert it from and to (ING-004).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The source version is required, not optional</strong> — for the same reason
/// <see cref="ScaleRecipeViewModel.SourceVersionNumber"/> is: a calculation reasons about content it was
/// explicitly shown, never about whatever happens to be live. Unlike scaling, this conversion does not read
/// any ingredient line from that version — the version exists so the preview carries the same provenance and
/// workspace-isolation guarantee every calculation route does.
/// </para>
/// <para>Read-only: nothing here is persisted, and nothing about the recipe changes because a quantity was converted.</para>
/// </remarks>
public sealed record ConvertUnitsViewModel(int SourceVersionNumber, decimal Quantity, Guid FromUnitId, Guid ToUnitId);

/// <summary>Shape validation for <see cref="ConvertUnitsViewModel"/>.</summary>
/// <remarks>
/// Whether the two units are actually usable, and whether they can be bridged at all, are checked further
/// down — the Facade resolves both ids (a cross-module lookup a validator cannot make), and Business runs
/// <c>UnitConversionCalculator</c>, which is where "compatible" is actually decided (ING-004's restriction:
/// no cross-dimension conversion without an approved density, and no temperature here — see 7.8 instead).
/// </remarks>
public sealed class ConvertUnitsViewModelValidator : AbstractValidator<ConvertUnitsViewModel>
{
    public ConvertUnitsViewModelValidator()
    {
        RuleFor(model => model.SourceVersionNumber)
            .GreaterThanOrEqualTo(RecipePolicy.FirstVersionNumber)
                .WithMessage($"A version number starts at {RecipePolicy.FirstVersionNumber}.")
            .OverridePropertyName(nameof(ConvertUnitsViewModel.SourceVersionNumber));

        RuleFor(model => model.Quantity)
            .GreaterThan(0m)
                .WithMessage("The quantity must be greater than zero.")
            .OverridePropertyName(nameof(ConvertUnitsViewModel.Quantity));

        RuleFor(model => model.FromUnitId)
            .NotEqual(Guid.Empty)
                .WithMessage("Name the unit to convert from.")
            .OverridePropertyName(nameof(ConvertUnitsViewModel.FromUnitId));

        RuleFor(model => model.ToUnitId)
            .NotEqual(Guid.Empty)
                .WithMessage("Name the unit to convert to.")
            .OverridePropertyName(nameof(ConvertUnitsViewModel.ToUnitId));
    }
}

/// <summary>
/// A unit-conversion request as Business runs it: the Facade has already resolved both units (a cross-module
/// lookup Business itself may not make — backend.md), so this carries their full metadata rather than ids.
/// </summary>
public sealed record RecipeUnitConversionRequest(
    Quantity Quantity, MeasurementUnitServiceModel FromUnit, MeasurementUnitServiceModel ToUnit);

/// <summary>
/// A computed unit-conversion preview for one explicit recipe version — a proposal only; nothing here is
/// persisted or changes the recipe (recipes.md).
/// </summary>
/// <param name="SourceVersionNumber">The version the preview was computed in the context of, echoed back as provenance.</param>
/// <param name="Result">
/// The conversion itself — method, exact and rounded quantities, formula, and source — computed by
/// <c>UnitConversionCalculator</c> (7.7). Reused as-is rather than re-shaped.
/// </param>
public sealed record RecipeUnitConversionResultServiceModel(int SourceVersionNumber, UnitConversionResult Result);
