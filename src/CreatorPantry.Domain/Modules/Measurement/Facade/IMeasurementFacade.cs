using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Measurement.Data;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Measurement.Facade;

public interface IMeasurementFacade
{
    Task<OperationResult<CursorPageServiceModel<MeasurementUnitServiceModel>>> ListUnitsAsync(
        MeasurementUnitQueryViewModel model, CancellationToken cancellationToken);
}
