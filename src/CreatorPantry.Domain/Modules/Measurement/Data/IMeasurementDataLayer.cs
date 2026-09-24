using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Measurement.Data;

/// <summary>Composes the unit repository into the data operations the module asks for.</summary>
/// <remarks>
/// Thin by nature rather than by omission: the read is a single query against one global catalogue, so there
/// is nothing to compose and no transaction to own. The layer exists so the seam is uniform.
/// </remarks>
public interface IMeasurementDataLayer
{
    Task<CreatorPantry.Domain.Managers.Reference.MeasurementDimension?> FindUsableUnitDimensionAsync(
        Guid unitId, CancellationToken cancellationToken);

    Task<(IReadOnlyList<MeasurementUnitRecord> Rows, bool HasMore)> ListUnitsAsync(
        MeasurementUnitQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyList<UnitMatchIndexEntry>> LoadMatchIndexAsync(CancellationToken cancellationToken);
}

