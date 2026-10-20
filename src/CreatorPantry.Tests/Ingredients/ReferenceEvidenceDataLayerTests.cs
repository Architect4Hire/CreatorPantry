using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Modules.Ingredients.Data;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Gateways;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Tests.Reference;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.Ingredients;

/// <summary>
/// Evidence through the shared cache (14.1): what is cached, under which global key, for how long it keeps its
/// retrieval date, and what is never cached.
/// </summary>
public sealed class ReferenceEvidenceDataLayerTests
{
    private static readonly DateTimeOffset RetrievedAt = new(2037, 10, 20, 9, 30, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_found_food_is_cached_under_a_global_key_and_the_second_read_keeps_its_retrieval_date()
    {
        var gateway = new ScriptedGateway { Food = Found("169761") };
        var cache = new FakeApplicationCache();
        var data = new ReferenceEvidenceDataLayer(gateway, cache);

        var first = await data.GetFoodAsync("169761", Ct);
        var second = await data.GetFoodAsync("169761", Ct);

        Assert.Equal(1, gateway.FoodReads);
        Assert.Equal(["global:reference:evidence:usda-fdc:v1:food:169761"], cache.Writes);
        Assert.Same(first, second);
        Assert.Equal(RetrievedAt, second.Food!.Provenance.RetrievedAt);
    }

    [Theory]
    [InlineData(ReferenceEvidenceOutcome.NotFound)]
    [InlineData(ReferenceEvidenceOutcome.NoApprovedSource)]
    [InlineData(ReferenceEvidenceOutcome.Throttled)]
    [InlineData(ReferenceEvidenceOutcome.CredentialRejected)]
    [InlineData(ReferenceEvidenceOutcome.Unavailable)]
    [InlineData(ReferenceEvidenceOutcome.Unreadable)]
    public async Task Anything_but_an_answer_is_not_cached_and_the_next_read_asks_again(ReferenceEvidenceOutcome outcome)
    {
        var gateway = new ScriptedGateway { Food = ReferenceFoodFetch.Of(outcome) };
        var cache = new FakeApplicationCache();
        var data = new ReferenceEvidenceDataLayer(gateway, cache);

        await data.GetFoodAsync("169761", Ct);
        await data.GetFoodAsync("169761", Ct);

        Assert.Equal(2, gateway.FoodReads);
        Assert.Empty(cache.Writes);
    }

    [Fact]
    public async Task An_unconfigured_host_touches_no_cache()
    {
        var cache = new FakeApplicationCache();
        var data = new ReferenceEvidenceDataLayer(new UnconfiguredReferenceEvidenceGateway(), cache);

        Assert.Equal(ReferenceEvidenceOutcome.NotConfigured, (await data.GetFoodAsync("169761", Ct)).Outcome);
        Assert.Equal(ReferenceEvidenceOutcome.NotConfigured, (await data.SearchFoodsAsync(Search("flour"), Ct)).Outcome);

        Assert.Empty(cache.Reads);
        Assert.Empty(cache.Writes);
    }

    [Fact]
    public async Task An_id_that_is_not_one_never_becomes_a_cache_key()
    {
        var cache = new FakeApplicationCache();
        var data = new ReferenceEvidenceDataLayer(new ScriptedGateway { Food = ReferenceFoodFetch.Of(ReferenceEvidenceOutcome.NotFound) }, cache);

        await data.GetFoodAsync("1:global:other", Ct);

        Assert.Empty(cache.Reads);
    }

    [Fact]
    public async Task A_search_is_keyed_by_a_digest_that_ignores_case_and_surrounding_space_and_never_holds_the_term()
    {
        var gateway = new ScriptedGateway
        {
            Search = new ReferenceFoodSearchResult(ReferenceEvidenceOutcome.Found, [new("usda-fdc", "169761", "Wheat flour", "sr-legacy/2019-04-01", null)]),
        };
        var cache = new FakeApplicationCache();
        var data = new ReferenceEvidenceDataLayer(gateway, cache);

        await data.SearchFoodsAsync(Search("All-Purpose Flour"), Ct);
        await data.SearchFoodsAsync(Search("  all-purpose flour "), Ct);

        Assert.Equal(1, gateway.SearchReads);
        var key = Assert.Single(cache.Writes);
        Assert.Matches("^global:reference:evidence:usda-fdc:v1:search:[0-9a-f]{64}$", key);
        Assert.DoesNotContain("flour", key, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_evidence_key_can_carry_a_workspace()
    {
        var cache = new FakeApplicationCache();
        var data = new ReferenceEvidenceDataLayer(
            new ScriptedGateway
            {
                Food = Found("169761"),
                Search = new ReferenceFoodSearchResult(ReferenceEvidenceOutcome.Found, []),
            },
            cache);

        await data.GetFoodAsync("169761", Ct);
        await data.SearchFoodsAsync(Search("flour"), Ct);

        Assert.All(cache.Reads.Concat(cache.Writes), key =>
        {
            Assert.StartsWith("global:", key, StringComparison.Ordinal);
            Assert.DoesNotContain("workspace", key, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Evidence_survives_the_real_caches_serialization_intact()
    {
        var cache = new DistributedApplicationCache(
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            NullLogger<DistributedApplicationCache>.Instance);
        var gateway = new ScriptedGateway { Food = Found("169761") };
        var data = new ReferenceEvidenceDataLayer(gateway, cache);

        var original = await data.GetFoodAsync("169761", Ct);
        var cached = await data.GetFoodAsync("169761", Ct);

        Assert.Equal(1, gateway.FoodReads);
        Assert.NotSame(original, cached);
        Assert.Equal(original.Outcome, cached.Outcome);
        Assert.Equal(original.Food!.Provenance, cached.Food!.Provenance);
        Assert.Equal(original.Food.Description, cached.Food.Description);
        Assert.Equal(original.Food.Nutrients, cached.Food.Nutrients);
        Assert.Equal(original.Food.Portions, cached.Food.Portions);
        Assert.True(cached.Food.Portions[0].IsDensity);
    }

    private static ReferenceFoodSearch Search(string canonicalName) =>
        ReferenceFoodSearch.ForIngredient(new Ingredient { CanonicalName = canonicalName });

    private static ReferenceFoodFetch Found(string id)
    {
        var source = EvidenceSourceCatalog.UsdaFoodDataCentral;

        return new ReferenceFoodFetch(
            ReferenceEvidenceOutcome.Found,
            new ReferenceFoodEvidence(
                new EvidenceProvenance(
                    source.Code, source.Name, source.Kind, "sr-legacy/2019-04-01", source.LicenseCode, source.LicenseUrl,
                    source.Citation, id, new DateOnly(2019, 4, 1), RetrievedAt),
                "Wheat flour, white, all-purpose, unenriched",
                [new NutrientEvidence(NutrientCode.Energy, 364.0m, NutrientUnit.Kilocalorie, "Energy", "Calculated")],
                [new PortionEvidence(1.0m, 125.0m, "cup-us", "sifted", "cup, sifted")]));
    }

    /// <summary>A configured FDC gateway as far as coverage goes, answering whatever it is given.</summary>
    private sealed class ScriptedGateway : IReferenceEvidenceGateway
    {
        public ReferenceFoodFetch Food { get; init; } = ReferenceFoodFetch.Of(ReferenceEvidenceOutcome.NotFound);

        public ReferenceFoodSearchResult Search { get; init; } = ReferenceFoodSearchResult.Of(ReferenceEvidenceOutcome.Unavailable);

        public int FoodReads { get; private set; }

        public int SearchReads { get; private set; }

        public EvidenceCoverage Describe(ReferenceEvidenceKind kind) =>
            EvidenceCoverage.Of(kind, [EvidenceSourceCatalog.UsdaFoodDataCentral.Code]);

        public Task<ReferenceFoodFetch> GetFoodAsync(string externalRecordId, CancellationToken cancellationToken)
        {
            FoodReads++;
            return Task.FromResult(Food);
        }

        public Task<ReferenceFoodSearchResult> SearchFoodsAsync(ReferenceFoodSearch search, CancellationToken cancellationToken)
        {
            SearchReads++;
            return Task.FromResult(Search);
        }
    }
}
