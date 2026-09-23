using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Measurement.Data;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Measurement;

/// <summary>
/// Application boundary for reading the shared unit catalogue.
/// </summary>
/// <remarks>
/// Global and read-only. It takes no workspace, no user and no membership — this is the platform data zone,
/// readable before any workspace is resolved (tenancy.md), and there is no ownership to check.
/// </remarks>
public interface IMeasurementFacade
{
    Task<OperationResult<CursorPageServiceModel<MeasurementUnitServiceModel>>> ListUnitsAsync(
        MeasurementUnitQueryViewModel model, CancellationToken cancellationToken);
}

/// <summary>Unit catalogue rules: reading a page and mapping repository records into ServiceModels.</summary>
/// <remarks>
/// No authorization here, and that absence is the domain rule rather than an omission. Unit data has no
/// owner and no <c>WorkspaceId</c>, so there is nothing to scope a decision to.
/// </remarks>
public interface IMeasurementBusiness
{
    Task<CursorPageServiceModel<MeasurementUnitServiceModel>> ListUnitsAsync(
        MeasurementUnitQuery query, CancellationToken cancellationToken);
}

/// <summary>Composes the unit repository into the data operations the module asks for.</summary>
/// <remarks>
/// Thin by nature rather than by omission: the read is a single query against one global catalogue, so there
/// is nothing to compose and no transaction to own. The layer exists so the seam is uniform.
/// </remarks>
public interface IMeasurementDataLayer
{
    Task<(IReadOnlyList<MeasurementUnitRecord> Rows, bool HasMore)> ListUnitsAsync(
        MeasurementUnitQuery query, CancellationToken cancellationToken);
}

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

internal sealed class MeasurementBusiness(IMeasurementDataLayer dataLayer) : IMeasurementBusiness
{
    public async Task<CursorPageServiceModel<MeasurementUnitServiceModel>> ListUnitsAsync(
        MeasurementUnitQuery query, CancellationToken cancellationToken)
    {
        var (rows, hasMore) = await dataLayer.ListUnitsAsync(query, cancellationToken);

        return PageBuilder.Build(rows, hasMore, query.Scope, row => new MeasurementUnitServiceModel(
            row.Id, row.Code, row.DisplayName, row.PluralName, row.Abbreviation,
            row.Dimension, row.System, row.BaseUnitFactor, row.DisplayPrecision));
    }
}

internal sealed class MeasurementDataLayer(IMeasurementUnitRepository units) : IMeasurementDataLayer
{
    public Task<(IReadOnlyList<MeasurementUnitRecord> Rows, bool HasMore)> ListUnitsAsync(
        MeasurementUnitQuery query, CancellationToken cancellationToken) =>
        units.ListAsync(query, cancellationToken);
}
