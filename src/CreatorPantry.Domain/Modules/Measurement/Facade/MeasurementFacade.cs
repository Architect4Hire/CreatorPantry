using CreatorPantry.Domain.Modules.Measurement.Business;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Measurement.Data;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Measurement.Facade;

internal sealed class MeasurementFacade(
    IValidator<MeasurementUnitQueryViewModel> validator,
    IMeasurementBusiness business,
    CachedPageReader reader) : IMeasurementFacade
{
    /// <summary>The cache-key resource literal. Unchanged by the module split, so cached keys stay identical.</summary>
    private const string Resource = "units";

    public Task<OperationResult<CursorPageServiceModel<MeasurementUnitServiceModel>>> ListUnitsAsync(
        MeasurementUnitQueryViewModel model, CancellationToken cancellationToken) =>
        reader.ReadAsync<MeasurementUnitQueryViewModel, MeasurementUnitQuery, MeasurementUnitServiceModel>(
            validator,
            model,
            Resource,
            MeasurementQueryFactory.TryCreate,
            query => query.CacheKeySegment,
            business.ListUnitsAsync,
            cancellationToken);
}

