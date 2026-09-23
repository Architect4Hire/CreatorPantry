using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Reference;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Measurement.Managers;

/// <summary>
/// Shape rules for a unit list query.
/// </summary>
/// <remarks>
/// <see cref="MeasurementUnitQueryViewModel.Limit"/> is deliberately not validated: an out-of-range page size
/// is clamped into the published bounds rather than rejected, so asking for a thousand rows returns a hundred
/// instead of a 400. A search term below the minimum length is likewise ignored, not refused. The cursor
/// <em>is</em> validated, because there is no sensible answer to a corrupt one — silently treating it as the
/// first page would restart a paging loop from the top without saying so.
/// </remarks>
public sealed class MeasurementUnitQueryViewModelValidator : AbstractValidator<MeasurementUnitQueryViewModel>
{
    public MeasurementUnitQueryViewModelValidator()
    {
        RuleFor(model => model.Search)
            .MaximumLength(ReferencePolicy.SearchMaxLength)
            .WithMessage($"Search terms are limited to {ReferencePolicy.SearchMaxLength} characters.")
            .OverridePropertyName(nameof(MeasurementUnitQueryViewModel.Search));

        RuleFor(model => model.Cursor)
            .Must(value => ReferenceCursor.TryDecode(value, out _))
            .WithMessage("Pass the previous page's nextCursor exactly as it was returned.")
            .When(model => !string.IsNullOrEmpty(model.Cursor))
            .OverridePropertyName(nameof(MeasurementUnitQueryViewModel.Cursor));

        // ReferencePolicy.ParseDimension, not Enum.TryParse: the latter also accepts "99" and "Mass,Volume".
        RuleFor(model => model.Dimension)
            .Must(MeasurementPolicy.IsAcceptedDimension)
            .WithMessage($"Enter one of: {string.Join(", ", Enum.GetNames<MeasurementDimension>())}.")
            .When(model => !string.IsNullOrWhiteSpace(model.Dimension))
            .OverridePropertyName(nameof(MeasurementUnitQueryViewModel.Dimension));
    }
}

/// <summary>
/// Turns a validated unit view model into the effective domain query.
/// </summary>
/// <remarks>
/// One derivation, called once per request: the facade names its cache entry from the result and hands the
/// same object to Business, so the rows a key stands for and the key itself cannot be computed from different
/// rules. The one thing a validator cannot check is whether a structurally valid cursor belongs to this query,
/// because the validator does not know the route — hence <c>TryCreate</c> rather than <c>Create</c>.
/// </remarks>
public static class MeasurementQueryFactory
{
    public static bool TryCreate(MeasurementUnitQueryViewModel model, string resource, out MeasurementUnitQuery query)
    {
        query = default!;

        var search = ReferencePolicy.NormalizeSearch(model.Search);
        var dimension = MeasurementPolicy.ParseDimension(model.Dimension);
        var scope = ReferenceQueryKey.Scope(resource, search, ("dimension", dimension?.ToString()));

        if (!ReferenceCursor.TryResolve(model.Cursor, scope, out var cursor))
        {
            return false;
        }

        query = new MeasurementUnitQuery(search, dimension, cursor, ReferencePolicy.ClampPageSize(model.Limit), scope);

        return true;
    }
}
