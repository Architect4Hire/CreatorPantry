using CreatorPantry.Domain.Modules.Measurement.Data;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Measurement.Business;

internal sealed class MeasurementBusiness(IMeasurementDataLayer dataLayer) : IMeasurementBusiness
{
    public Task<CreatorPantry.Domain.Managers.Reference.MeasurementDimension?> FindUsableUnitDimensionAsync(
        Guid unitId, CancellationToken cancellationToken) =>
        dataLayer.FindUsableUnitDimensionAsync(unitId, cancellationToken);

    public async Task<CursorPageServiceModel<MeasurementUnitServiceModel>> ListUnitsAsync(
        MeasurementUnitQuery query, CancellationToken cancellationToken)
    {
        var (rows, hasMore) = await dataLayer.ListUnitsAsync(query, cancellationToken);

        return PageBuilder.Build(rows, hasMore, query.Scope, row => new MeasurementUnitServiceModel(
            row.Id, row.Code, row.DisplayName, row.PluralName, row.Abbreviation,
            row.Dimension, row.System, row.BaseUnitFactor, row.DisplayPrecision));
    }

    public async Task<IReadOnlyList<MeasurementUnitServiceModel>> FindUnitsByIdsAsync(
        IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken)
    {
        var rows = await dataLayer.FindUnitsByIdsAsync(unitIds, cancellationToken);

        return rows.Select(row => new MeasurementUnitServiceModel(
            row.Id, row.Code, row.DisplayName, row.PluralName, row.Abbreviation,
            row.Dimension, row.System, row.BaseUnitFactor, row.DisplayPrecision)).ToList();
    }

    public Task<IReadOnlyList<UnitMatchIndexEntry>> LoadMatchIndexAsync(CancellationToken cancellationToken) =>
        dataLayer.LoadMatchIndexAsync(cancellationToken);

    public IReadOnlyList<UnitMatchResult> ResolveCandidates(
        IReadOnlyList<string> candidateTexts, IReadOnlyList<UnitMatchIndexEntry> index)
    {
        var lookup = index.ToLookup(entry => entry.NormalizedText, StringComparer.Ordinal);

        return candidateTexts.Select(text => UnitMatcher.Resolve(text, lookup)).ToList();
    }
}

