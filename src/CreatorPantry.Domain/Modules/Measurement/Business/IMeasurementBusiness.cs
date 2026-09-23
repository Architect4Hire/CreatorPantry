using CreatorPantry.Domain.Modules.Measurement.Data;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Measurement.Business;

/// <summary>Unit catalogue rules: reading a page and mapping repository records into ServiceModels.</summary>
/// <remarks>
/// No authorization here, and that absence is the domain rule rather than an omission. Unit data has no
/// owner and no <c>WorkspaceId</c>, so there is nothing to scope a decision to.
/// </remarks>
public interface IMeasurementBusiness
{
    Task<CreatorPantry.Domain.Managers.Reference.MeasurementDimension?> FindUsableUnitDimensionAsync(
        Guid unitId, CancellationToken cancellationToken);

    Task<CursorPageServiceModel<MeasurementUnitServiceModel>> ListUnitsAsync(
        MeasurementUnitQuery query, CancellationToken cancellationToken);
}
