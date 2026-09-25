using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Measurement.Data;

internal sealed class MeasurementDataLayer(IMeasurementUnitRepository units) : IMeasurementDataLayer
{
    public Task<CreatorPantry.Domain.Managers.Reference.MeasurementDimension?> FindUsableUnitDimensionAsync(
        Guid unitId, CancellationToken cancellationToken) =>
        units.FindUsableUnitDimensionAsync(unitId, cancellationToken);

    public Task<(IReadOnlyList<MeasurementUnitRecord> Rows, bool HasMore)> ListUnitsAsync(
        MeasurementUnitQuery query, CancellationToken cancellationToken) =>
        units.ListAsync(query, cancellationToken);

    public Task<IReadOnlyList<MeasurementUnitRecord>> FindUnitsByIdsAsync(
        IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken) =>
        units.FindByIdsAsync(unitIds, cancellationToken);

    public Task<IReadOnlyList<UnitMatchIndexEntry>> LoadMatchIndexAsync(CancellationToken cancellationToken) =>
        units.ListMatchIndexAsync(cancellationToken);
}
