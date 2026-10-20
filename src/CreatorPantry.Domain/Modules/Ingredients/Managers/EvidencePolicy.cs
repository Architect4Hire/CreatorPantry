using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Paging;

namespace CreatorPantry.Domain.Modules.Ingredients.Managers;

/// <summary>
/// Limits and cache rules for external reference evidence (14.1). Global reference data: no
/// <c>WorkspaceId</c>, and no cache key here can carry one (tenancy.md).
/// </summary>
public static class EvidencePolicy
{
    /// <summary>Bumped when a cached evidence shape changes, so an old entry is a miss rather than a misread.</summary>
    public const string CacheSchemaVersion = "v1";

    /// <summary>
    /// How long one food's evidence is reused. Sources publish a few times a year, and every cached value
    /// carries the instant it was retrieved, so a reader can always say how old it is.
    /// </summary>
    public static readonly TimeSpan FoodCacheLifetime = TimeSpan.FromHours(24);

    /// <summary>Shorter than a food's: a search's ranking moves when a source adds foods.</summary>
    public static readonly TimeSpan SearchCacheLifetime = TimeSpan.FromHours(1);

    /// <summary>Candidates a search returns. Enough to choose among; a creator confirms one, not a page of them.</summary>
    public const int MaxSearchResults = 10;

    public const int SearchTermMaxLength = 200;

    public const int ExternalRecordIdMaxLength = 32;

    public const int DescriptionMaxLength = 512;

    public const int SourceLabelMaxLength = 128;

    /// <summary>The one unit each nutrient is recorded in. See <see cref="NutrientCode"/>.</summary>
    public static NutrientUnit UnitOf(NutrientCode code) => code switch
    {
        NutrientCode.Energy => NutrientUnit.Kilocalorie,
        NutrientCode.Cholesterol or NutrientCode.Sodium or NutrientCode.Potassium
            or NutrientCode.Calcium or NutrientCode.Iron => NutrientUnit.Milligram,
        NutrientCode.VitaminD => NutrientUnit.Microgram,
        _ => NutrientUnit.Gram,
    };

    /// <summary><c>global:reference:evidence:{source}:v1:food:{id}</c>.</summary>
    public static string FoodCacheKey(string sourceCode, string externalRecordId) =>
        CacheKeys.Global(ReferencePolicy.CacheCategory, $"evidence:{sourceCode}:{CacheSchemaVersion}:food:{externalRecordId}");

    /// <summary>
    /// <c>global:reference:evidence:{source}:v1:search:{digest}</c>. The term is digested rather than spliced in,
    /// so a key's length and alphabet never depend on what was searched for.
    /// </summary>
    public static string SearchCacheKey(string sourceCode, string termDigest) =>
        CacheKeys.Global(ReferencePolicy.CacheCategory, $"evidence:{sourceCode}:{CacheSchemaVersion}:search:{termDigest}");
}
