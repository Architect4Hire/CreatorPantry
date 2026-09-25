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

    /// <summary>The active units among the ids given, in no particular order. Fewer than asked for is a normal answer.</summary>
    Task<IReadOnlyList<MeasurementUnitServiceModel>> FindUnitsByIdsAsync(
        IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken);

    /// <summary>Loads the flattened match index the facade caches and passes back into <see cref="ResolveCandidates"/>.</summary>
    Task<IReadOnlyList<UnitMatchIndexEntry>> LoadMatchIndexAsync(CancellationToken cancellationToken);

    /// <summary>Resolves each candidate against an already-loaded index. Pure: no I/O, no caching decision.</summary>
    IReadOnlyList<UnitMatchResult> ResolveCandidates(
        IReadOnlyList<string> candidateTexts, IReadOnlyList<UnitMatchIndexEntry> index);
}
