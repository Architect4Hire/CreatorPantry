using CreatorPantry.Domain.Modules.Measurement.Business;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Measurement.Data;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Measurement.Facade;

internal sealed class MeasurementFacade(
    IValidator<MeasurementUnitQueryViewModel> validator,
    IMeasurementBusiness business,
    IApplicationCache cache,
    CachedPageReader reader) : IMeasurementFacade
{
    /// <summary>The cache-key resource literal. Unchanged by the module split, so cached keys stay identical.</summary>
    private const string Resource = "units";

    public Task<CreatorPantry.Domain.Managers.Reference.MeasurementDimension?> FindUsableUnitDimensionAsync(
        Guid unitId, CancellationToken cancellationToken) =>
        business.FindUsableUnitDimensionAsync(unitId, cancellationToken);

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

    public Task<IReadOnlyList<MeasurementUnitServiceModel>> FindUnitsByIdsAsync(
        IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken) =>
        business.FindUnitsByIdsAsync(unitIds, cancellationToken);

    public async Task<OperationResult<IReadOnlyList<UnitMatchResult>>> ResolveCandidatesAsync(
        IReadOnlyList<string> candidateTexts, CancellationToken cancellationToken)
    {
        if (candidateTexts.Count == 0)
        {
            return OperationResult<IReadOnlyList<UnitMatchResult>>.Success([]);
        }

        if (candidateTexts.Count > ReferencePolicy.MaxMatchCandidates
            || candidateTexts.Any(text => text.Length > ReferencePolicy.MaxMatchCandidateLength))
        {
            return OperationResult<IReadOnlyList<UnitMatchResult>>.Failure(OperationError.Validation(
                ReferenceErrorCodes.CandidatesInvalid,
                "The candidates could not be accepted as submitted.",
                [("CandidateTexts",
                    $"Submit no more than {ReferencePolicy.MaxMatchCandidates} candidates, "
                        + $"each no longer than {ReferencePolicy.MaxMatchCandidateLength} characters.")]));
        }

        var index = await cache.GetAsync<IReadOnlyList<UnitMatchIndexEntry>>(MatchIndexCacheKey, cancellationToken);
        if (index is null)
        {
            index = await business.LoadMatchIndexAsync(cancellationToken);
            await cache.SetAsync(MatchIndexCacheKey, index, ReferencePolicy.CacheLifetime, cancellationToken);
        }

        return OperationResult<IReadOnlyList<UnitMatchResult>>.Success(
            business.ResolveCandidates(candidateTexts, index));
    }

    /// <summary>
    /// One global key for the whole flattened match index, not one per candidate: the catalogue is small
    /// enough to hold in memory entirely, and resolving a batch of candidates against it costs one cache read
    /// instead of one per line.
    /// </summary>
    private static string MatchIndexCacheKey =>
        CacheKeys.Global(ReferencePolicy.CacheCategory, $"{Resource}:match-index:{ReferencePolicy.CacheSchemaVersion}");
}

