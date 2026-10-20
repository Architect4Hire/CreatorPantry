namespace CreatorPantry.Domain.Modules.Ingredients.Managers;

/// <summary>
/// One external source the product has approved as evidence, with the licence that approval rests on.
/// </summary>
/// <param name="Code">The <c>ReferenceSource.Code</c> it is recorded under. Permanent.</param>
/// <param name="LicenseCode">An SPDX identifier, e.g. <c>CC0-1.0</c>.</param>
/// <param name="Covers">
/// The evidence kinds this source is approved for. Nothing outside this list is read from it, even where the
/// source happens to publish something that looks like it.
/// </param>
/// <param name="DataSets">
/// The source's own data sets that are approved, by the key recorded in a fact's source version. A record from
/// any other data set of the same source is not approved evidence.
/// </param>
public sealed record ApprovedEvidenceSource(
    string Code,
    string Name,
    ReferenceSourceKind Kind,
    string Url,
    string Citation,
    string LicenseCode,
    string LicenseUrl,
    IReadOnlyList<ReferenceEvidenceKind> Covers,
    IReadOnlyList<string> DataSets);

/// <summary>
/// The approved evidence sources (14.1, B-33). The one list that both the seeded <c>ReferenceSource</c> rows and
/// the provider adapters read, so the licence a fact is recorded under and the licence its adapter was approved
/// on cannot drift apart.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Adding a source is a decision, not a code change.</strong> A source enters this list only after its
/// current terms and licence have been read and recorded in <c>docs/architecture-decisions/baseline.md</c>.
/// No source is scraped, and nothing reaches this list because it was convenient to fetch.
/// </para>
/// <para>
/// <strong>An evidence kind with no source here has no evidence.</strong> Allergen, dietary and food-safety
/// evidence have none today, and the answer for each is "no approved source" — which callers read as unknown,
/// never as absent (recipes.md).
/// </para>
/// </remarks>
public static class EvidenceSourceCatalog
{
    /// <summary>FoodData Central's Foundation Foods data set.</summary>
    public const string FdcFoundation = "foundation";

    /// <summary>FoodData Central's SR Legacy data set — frozen in 2018, still the broadest set of household portions.</summary>
    public const string FdcSrLegacy = "sr-legacy";

    /// <summary>
    /// USDA FoodData Central, approved for nutrition and density (B-09, B-33). Public domain under CC0 1.0. The
    /// citation is the one USDA suggests, with the "[cited (enter date)]" clause left out deliberately: every
    /// fact records its own retrieval and publication dates, so a date in the citation string would be a
    /// second, staler copy. Updated in 14.7 to USDA's current wording, which names the Beltsville Human
    /// Nutrition Research Center and gives the URL in full.
    /// </summary>
    /// <remarks>
    /// Branded, Survey (FNDDS) and Experimental foods are not approved: Branded is manufacturer label data,
    /// Survey foods are mostly prepared dishes rather than ingredients, and Experimental is research data.
    /// </remarks>
    public static readonly ApprovedEvidenceSource UsdaFoodDataCentral = new(
        Code: "usda-fdc",
        Name: "USDA FoodData Central",
        Kind: ReferenceSourceKind.OfficialDatabase,
        Url: "https://fdc.nal.usda.gov/",
        Citation: "U.S. Department of Agriculture, Agricultural Research Service, Beltsville Human Nutrition "
            + "Research Center. FoodData Central. https://fdc.nal.usda.gov/.",
        LicenseCode: "CC0-1.0",
        LicenseUrl: "https://creativecommons.org/publicdomain/zero/1.0/",
        Covers: [ReferenceEvidenceKind.Nutrition, ReferenceEvidenceKind.Density],
        DataSets: [FdcFoundation, FdcSrLegacy]);

    public static IReadOnlyList<ApprovedEvidenceSource> All { get; } = [UsdaFoodDataCentral];

    /// <summary>The approved source for <paramref name="kind"/>, or null when there is none.</summary>
    public static ApprovedEvidenceSource? For(ReferenceEvidenceKind kind) =>
        All.FirstOrDefault(source => source.Covers.Contains(kind));
}
