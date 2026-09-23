using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Measurement.Data;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Measurement.Facade;

public interface IMeasurementFacade
{
    /// <summary>
    /// The dimension of a unit still offered for new input, or <c>null</c> when the id names no such unit.
    /// </summary>
    /// <remarks>
    /// Exists so another module can both validate a unit reference and learn its dimension in one call — a
    /// recipe has to store the dimension alongside the unit id so the composite foreign key can pin the two
    /// together, and it cannot read this table itself. A retired or unknown unit answers null.
    /// </remarks>
    Task<CreatorPantry.Domain.Managers.Reference.MeasurementDimension?> FindUsableUnitDimensionAsync(
        Guid unitId, CancellationToken cancellationToken);

    Task<OperationResult<CursorPageServiceModel<MeasurementUnitServiceModel>>> ListUnitsAsync(
        MeasurementUnitQueryViewModel model, CancellationToken cancellationToken);
}
