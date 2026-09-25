using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// A display-normalization preview request: which recipe version it is asked in the context of, the value (or
/// range) to render, the unit to render it in, and the presentation choices <c>QuantityDisplayCalculator</c>
/// takes (ING-006).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The source version is required</strong>, for the reason <see cref="ScaleRecipeViewModel.SourceVersionNumber"/>
/// is — see <see cref="ConvertUnitsViewModel"/>'s remarks.
/// </para>
/// <para>
/// <strong>Presentation only.</strong> Nothing here changes a canonical quantity or a recipe version
/// (recipes.md, ING-006) — this renders whatever value the caller sends, once, and returns text.
/// </para>
/// </remarks>
public sealed record NormalizeDisplayViewModel(
    int SourceVersionNumber,
    decimal Value,
    decimal? UpperValue,
    Guid UnitId,
    int Precision,
    bool UseAbbreviation,
    MidpointRounding Rounding);

/// <summary>Shape validation for <see cref="NormalizeDisplayViewModel"/>.</summary>
/// <remarks>
/// Whether the unit is actually usable is checked further down — the Facade resolves <see cref="NormalizeDisplayViewModel.UnitId"/>,
/// a cross-module lookup a validator cannot make.
/// </remarks>
public sealed class NormalizeDisplayViewModelValidator : AbstractValidator<NormalizeDisplayViewModel>
{
    public NormalizeDisplayViewModelValidator()
    {
        RuleFor(model => model.SourceVersionNumber)
            .GreaterThanOrEqualTo(RecipePolicy.FirstVersionNumber)
                .WithMessage($"A version number starts at {RecipePolicy.FirstVersionNumber}.")
            .OverridePropertyName(nameof(NormalizeDisplayViewModel.SourceVersionNumber));

        RuleFor(model => model.Value)
            .GreaterThanOrEqualTo(0m)
                .WithMessage("The value cannot be negative.")
            .OverridePropertyName(nameof(NormalizeDisplayViewModel.Value));

        RuleFor(model => model.UpperValue)
            .GreaterThan(model => model.Value)
                .WithMessage("The upper value must be greater than the value.")
            .When(model => model.UpperValue is not null)
            .OverridePropertyName(nameof(NormalizeDisplayViewModel.UpperValue));

        RuleFor(model => model.UnitId)
            .NotEqual(Guid.Empty)
                .WithMessage("Name the unit to render.")
            .OverridePropertyName(nameof(NormalizeDisplayViewModel.UnitId));

        RuleFor(model => model.Precision)
            .GreaterThanOrEqualTo(0)
                .WithMessage("Precision cannot be negative.")
            .OverridePropertyName(nameof(NormalizeDisplayViewModel.Precision));

        RuleFor(model => model.Rounding)
            .IsInEnum()
                .WithMessage("That rounding mode is not recognized.")
            .OverridePropertyName(nameof(NormalizeDisplayViewModel.Rounding));
    }
}

/// <summary>
/// A display-normalization request as Business runs it: the Facade has already resolved the unit (a
/// cross-module lookup Business itself may not make — backend.md), so this carries its full metadata rather
/// than an id.
/// </summary>
public sealed record RecipeQuantityDisplayRequest(
    Quantity Value,
    Quantity? UpperValue,
    MeasurementUnitServiceModel Unit,
    int Precision,
    bool UseAbbreviation,
    MidpointRounding Rounding);

/// <summary>
/// A computed display-normalization preview for one explicit recipe version — presentation only; nothing here
/// is persisted, and no canonical quantity or recipe version changes (recipes.md, ING-006).
/// </summary>
/// <param name="SourceVersionNumber">The version the preview was computed in the context of, echoed back as provenance.</param>
/// <param name="Result">
/// The rendering itself, computed by <c>QuantityDisplayCalculator</c> (7.10). Reused as-is rather than re-shaped.
/// </param>
public sealed record RecipeQuantityDisplayResultServiceModel(int SourceVersionNumber, QuantityDisplayResult Result);
