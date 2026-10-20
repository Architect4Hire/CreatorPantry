using System.Security.Cryptography;
using System.Text;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Modules.Ingredients.Gateways;
using CreatorPantry.Domain.Modules.Ingredients.Managers;

namespace CreatorPantry.Domain.Modules.Ingredients.Data;

/// <inheritdoc cref="IReferenceEvidenceDataLayer"/>
internal sealed class ReferenceEvidenceDataLayer(
    IReferenceEvidenceGateway gateway,
    IApplicationCache cache) : IReferenceEvidenceDataLayer
{
    public EvidenceCoverage Describe(ReferenceEvidenceKind kind) => gateway.Describe(kind);

    public async Task<ReferenceFoodFetch> GetFoodAsync(string externalRecordId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(externalRecordId);

        // Foods are what nutrition and density both read; with no reachable source there is nothing to key by.
        if (ReachableSource() is not { } source
            || externalRecordId.Length is 0 or > EvidencePolicy.ExternalRecordIdMaxLength
            || !externalRecordId.All(char.IsAsciiDigit))
        {
            return await gateway.GetFoodAsync(externalRecordId, cancellationToken);
        }

        var key = EvidencePolicy.FoodCacheKey(source, externalRecordId);
        if (await cache.GetAsync<ReferenceFoodFetch>(key, cancellationToken) is { Outcome: ReferenceEvidenceOutcome.Found } cached)
        {
            return cached;
        }

        var fetched = await gateway.GetFoodAsync(externalRecordId, cancellationToken);
        if (fetched.Outcome is ReferenceEvidenceOutcome.Found)
        {
            await cache.SetAsync(key, fetched, EvidencePolicy.FoodCacheLifetime, cancellationToken);
        }

        return fetched;
    }

    public async Task<ReferenceFoodSearchResult> SearchFoodsAsync(ReferenceFoodSearch search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);

        if (ReachableSource() is not { } source)
        {
            return await gateway.SearchFoodsAsync(search, cancellationToken);
        }

        var key = EvidencePolicy.SearchCacheKey(source, Digest(search.Term));
        if (await cache.GetAsync<ReferenceFoodSearchResult>(key, cancellationToken) is { Outcome: ReferenceEvidenceOutcome.Found } cached)
        {
            return cached;
        }

        var found = await gateway.SearchFoodsAsync(search, cancellationToken);
        if (found.Outcome is ReferenceEvidenceOutcome.Found)
        {
            await cache.SetAsync(key, found, EvidencePolicy.SearchCacheLifetime, cancellationToken);
        }

        return found;
    }

    private string? ReachableSource() =>
        gateway.Describe(ReferenceEvidenceKind.Nutrition) is { Status: EvidenceCoverageStatus.Available, SourceCode: { } code }
            ? code
            : null;

    /// <summary>Case and surrounding space do not make a different search; anything else does.</summary>
    private static string Digest(string term) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(term.Trim().ToLowerInvariant())));
}
