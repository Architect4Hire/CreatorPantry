using CreatorPantry.Domain.Modules.Ingredients.Business;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ingredients.Data;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ingredients.Facade;
internal sealed class IngredientFacade(
    IValidator<IngredientQueryViewModel> validator,
    IIngredientBusiness business,
    IApplicationCache cache,
    CachedPageReader reader) : IIngredientFacade
{
    /// <summary>The cache-key resource literal. Unchanged by the module split, so cached keys stay identical.</summary>
    private const string Resource = "ingredients";

    public Task<OperationResult<CursorPageServiceModel<IngredientServiceModel>>> ListIngredientsAsync(
        IngredientQueryViewModel model, CancellationToken cancellationToken) =>
        reader.ReadAsync<IngredientQueryViewModel, IngredientQuery, IngredientServiceModel>(
            validator,
            model,
            Resource,
            IngredientQueryFactory.TryCreate,
            query => query.CacheKeySegment,
            business.ListIngredientsAsync,
            cancellationToken);

    public async Task<OperationResult<IReadOnlyList<IngredientMatchResult>>> ResolveCandidatesAsync(
        IReadOnlyList<string> candidateTexts, CancellationToken cancellationToken)
    {
        if (candidateTexts.Count == 0)
        {
            return OperationResult<IReadOnlyList<IngredientMatchResult>>.Success([]);
        }

        if (candidateTexts.Count > ReferencePolicy.MaxMatchCandidates
            || candidateTexts.Any(text => text.Length > ReferencePolicy.MaxMatchCandidateLength))
        {
            return OperationResult<IReadOnlyList<IngredientMatchResult>>.Failure(OperationError.Validation(
                ReferenceErrorCodes.CandidatesInvalid,
                "The candidates could not be accepted as submitted.",
                [("CandidateTexts",
                    $"Submit no more than {ReferencePolicy.MaxMatchCandidates} candidates, "
                        + $"each no longer than {ReferencePolicy.MaxMatchCandidateLength} characters.")]));
        }

        var index = await cache.GetAsync<IReadOnlyList<IngredientMatchIndexEntry>>(MatchIndexCacheKey, cancellationToken);
        if (index is null)
        {
            index = await business.LoadMatchIndexAsync(cancellationToken);
            await cache.SetAsync(MatchIndexCacheKey, index, ReferencePolicy.CacheLifetime, cancellationToken);
        }

        return OperationResult<IReadOnlyList<IngredientMatchResult>>.Success(
            business.ResolveCandidates(candidateTexts, index));
    }

    // Forwarded directly, with no validation and no cache: a single AnyAsync by primary key, exactly as
    // ControlledVocabularyRepository.IsUsableAsync answers the equivalent question for the four vocabularies.
    public Task<bool> IsUsableAsync(Guid ingredientId, CancellationToken cancellationToken) =>
        business.IsUsableAsync(ingredientId, cancellationToken);

    /// <summary>
    /// One global key for the whole flattened match index, not one per candidate: the catalogue is small
    /// enough to hold in memory entirely, and resolving a batch of candidates against it costs one cache read
    /// instead of one per line.
    /// </summary>
    private static string MatchIndexCacheKey =>
        CacheKeys.Global(ReferencePolicy.CacheCategory, $"{Resource}:match-index:{ReferencePolicy.CacheSchemaVersion}");
}

