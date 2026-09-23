using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Results;
using FluentValidation;

namespace CreatorPantry.Domain.Managers.Paging;

/// <summary>
/// The read path every cached, cursor-paged facade shares: validate the query, translate it once, serve it
/// from cache when possible, otherwise read through and cache the result.
/// </summary>
/// <remarks>
/// <para>
/// Extracted when the reference vertical became three modules. Each of them needs the identical sequence,
/// and the one thing that must not be copied three times is the rule that the cache key and the query handed
/// to Business come from the <em>same</em> translation — deriving them separately is what previously let one
/// key stand for two different result sets.
/// </para>
/// <para>
/// It lives in the shared kernel and depends on no module: everything it touches is a primitive
/// (<see cref="IApplicationCache"/>, <see cref="OperationResult{T}"/>, a validator, and two delegates the
/// caller supplies).
/// </para>
/// </remarks>
public sealed class CachedPageReader(IApplicationCache cache)
{
    /// <summary>Builds the effective query from a validated view model, or reports that its cursor does not belong.</summary>
    public delegate bool TryCreateQuery<in TViewModel, TQuery>(TViewModel model, string resource, out TQuery query);

    public async Task<OperationResult<CursorPageServiceModel<TModel>>> ReadAsync<TViewModel, TQuery, TModel>(
        IValidator<TViewModel> validator,
        TViewModel model,
        string resource,
        TryCreateQuery<TViewModel, TQuery> toQuery,
        Func<TQuery, string> toCacheKeySegment,
        Func<TQuery, CancellationToken, Task<CursorPageServiceModel<TModel>>> read,
        CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            // A bad cursor gets its own code, because it means something different to a client than a bad
            // filter does: stop paging and start over, rather than fix the input.
            var code = validation.Errors.Any(error =>
                error.PropertyName.Equals("Cursor", StringComparison.Ordinal))
                ? ReferenceErrorCodes.CursorInvalid
                : ReferenceErrorCodes.QueryInvalid;

            return OperationResult<CursorPageServiceModel<TModel>>.Failure(OperationError.Validation(
                code,
                "The request is invalid.",
                validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
        }

        // The one cursor failure a validator cannot see: structurally fine, but issued for another resource or
        // another filter. Accepting it would return a plausible page from the wrong position in the wrong set.
        if (!toQuery(model, resource, out var query))
        {
            return OperationResult<CursorPageServiceModel<TModel>>.Failure(OperationError.Validation(
                ReferenceErrorCodes.CursorInvalid,
                "The request is invalid.",
                [("Cursor",
                    "This cursor was issued for a different resource or different filters. Start again without one.")]));
        }

        var cacheKey = CacheKeyFor(resource, toCacheKeySegment(query));

        var cached = await cache.GetAsync<CursorPageServiceModel<TModel>>(cacheKey, cancellationToken);
        if (cached is not null)
        {
            return OperationResult<CursorPageServiceModel<TModel>>.Success(cached);
        }

        var page = await read(query, cancellationToken);
        await cache.SetAsync(cacheKey, page, ReferencePolicy.CacheLifetime, cancellationToken);

        return OperationResult<CursorPageServiceModel<TModel>>.Success(page);
    }

    /// <summary>
    /// A global cache key for one page of one resource.
    /// </summary>
    /// <remarks>
    /// <see cref="CacheKeys.Global"/>, never <see cref="CacheKeys.Workspace"/>. Reference data has no
    /// workspace to scope to and there is no overload that would accept one, so a reference page structurally
    /// cannot be cached under a tenant's key or read out from under another tenant's. The key strings are
    /// unchanged by the module split: the resource literal each facade passes is the same one it passed before.
    /// </remarks>
    private static string CacheKeyFor(string resource, string querySegment) =>
        CacheKeys.Global(
            ReferencePolicy.CacheCategory,
            $"{resource}:{ReferencePolicy.CacheSchemaVersion}:{querySegment}");
}
