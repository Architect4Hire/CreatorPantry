using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Measurement.Data;

internal sealed class MeasurementDataLayer(IMeasurementUnitRepository units) : IMeasurementDataLayer
{
    public Task<(IReadOnlyList<MeasurementUnitRecord> Rows, bool HasMore)> ListUnitsAsync(
        MeasurementUnitQuery query, CancellationToken cancellationToken) =>
        units.ListAsync(query, cancellationToken);
}
