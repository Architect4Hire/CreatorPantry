using CreatorPantry.Domain.Modules.Ingredients.Gateways;
using CreatorPantry.Domain.Modules.Ingredients.Managers;

namespace CreatorPantry.Domain.Modules.Ingredients.Data;

/// <summary>
/// External reference evidence, read through the shared cache (14.1). The one way above the gateway to reach an
/// evidence source: Business calls this, never the gateway.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Cache-first, and only for answers.</strong> A found food and a completed search are cached under
/// global keys (<see cref="EvidencePolicy.FoodCacheKey"/>); a throttle, an outage or a refusal is not, so the
/// next read asks again instead of repeating a failure for a day. Evidence is platform reference data, so no
/// key carries a workspace and none can (tenancy.md).
/// </para>
/// <para>
/// <strong>A cached answer keeps its retrieval date.</strong> <see cref="EvidenceProvenance.RetrievedAt"/> is
/// when the source was read, and a cache hit hands it back unchanged.
/// </para>
/// </remarks>
public interface IReferenceEvidenceDataLayer
{
    /// <inheritdoc cref="IReferenceEvidenceGateway.Describe"/>
    EvidenceCoverage Describe(ReferenceEvidenceKind kind);

    /// <inheritdoc cref="IReferenceEvidenceGateway.GetFoodAsync"/>
    Task<ReferenceFoodFetch> GetFoodAsync(string externalRecordId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IReferenceEvidenceGateway.SearchFoodsAsync"/>
    Task<ReferenceFoodSearchResult> SearchFoodsAsync(ReferenceFoodSearch search, CancellationToken cancellationToken);
}
