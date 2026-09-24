using CreatorPantry.Domain.Modules.Ingredients.Data;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Measurement.Data;
using CreatorPantry.Domain.Modules.Measurement.Managers;

namespace CreatorPantry.Tests.Reference;

/// <summary>
/// The real repository queries behind 7.3's matcher, against the actual seeded catalogue — the pure
/// <c>IngredientMatcher</c>/<c>UnitMatcher</c> tests cover the resolution algorithm in isolation; these prove
/// the index it runs against is actually built correctly from the database.
/// </summary>
public sealed class MatchIndexRepositoryTests
{
    [Fact]
    public async Task The_ingredient_index_includes_the_canonical_name_and_every_alias()
    {
        await using var fixture = await ReferenceCatalogFixture.StartAsync();
        await using var context = fixture.CreateContext();
        var repository = new IngredientRepository(context);

        var index = await repository.ListMatchIndexAsync(TestContext.Current.CancellationToken);
        var lookup = index.ToLookup(entry => entry.NormalizedText, StringComparer.Ordinal);

        // "cornflour" for cornstarch — the exact example IngredientAlias's own doc comment cites.
        var alias = Assert.Single(lookup["cornflour"]);
        Assert.Equal(IngredientMatchKind.Alias, alias.Kind);
        Assert.Equal("cornstarch", alias.CanonicalName);

        var canonical = Assert.Single(lookup["cornstarch"]);
        Assert.Equal(IngredientMatchKind.CanonicalName, canonical.Kind);
        Assert.Equal(alias.IngredientId, canonical.IngredientId);
    }

    [Fact]
    public async Task The_ingredient_index_resolves_a_hyphenated_canonical_name_through_normalized_text()
    {
        await using var fixture = await ReferenceCatalogFixture.StartAsync();
        await using var context = fixture.CreateContext();
        var repository = new IngredientRepository(context);

        var index = await repository.ListMatchIndexAsync(TestContext.Current.CancellationToken);
        var lookup = index.ToLookup(entry => entry.NormalizedText, StringComparer.Ordinal);

        var result = IngredientMatcher.Resolve("All-Purpose Flour", lookup);

        Assert.Equal("all-purpose flour", result.Resolved!.CanonicalName);
        Assert.Equal(IngredientMatchKind.CanonicalName, result.Resolved.Kind);
    }

    [Fact]
    public async Task A_plural_ingredient_alias_resolves_through_the_real_matcher()
    {
        await using var fixture = await ReferenceCatalogFixture.StartAsync();
        await using var context = fixture.CreateContext();
        var repository = new IngredientRepository(context);

        var index = await repository.ListMatchIndexAsync(TestContext.Current.CancellationToken);
        var lookup = index.ToLookup(entry => entry.NormalizedText, StringComparer.Ordinal);

        var result = IngredientMatcher.Resolve("scallions", lookup);

        Assert.Equal("green onion", result.Resolved!.CanonicalName);
        Assert.Equal(IngredientMatchKind.Alias, result.Resolved.Kind);
    }

    [Fact]
    public async Task The_unit_index_resolves_a_code_a_plural_and_an_alias_to_the_same_seeded_unit()
    {
        await using var fixture = await ReferenceCatalogFixture.StartAsync();
        await using var context = fixture.CreateContext();
        var repository = new MeasurementUnitRepository(context);

        var index = await repository.ListMatchIndexAsync(TestContext.Current.CancellationToken);
        var lookup = index.ToLookup(entry => entry.NormalizedText, StringComparer.Ordinal);

        var byCode = UnitMatcher.Resolve("g", lookup);
        var byPlural = UnitMatcher.Resolve("grams", lookup);

        Assert.Equal("gram", byCode.Resolved!.DisplayName);
        Assert.Equal(byCode.Resolved.MeasurementUnitId, byPlural.Resolved!.MeasurementUnitId);
    }

    [Fact]
    public async Task An_unrecognized_candidate_does_not_resolve_against_the_real_catalogue()
    {
        await using var fixture = await ReferenceCatalogFixture.StartAsync();
        await using var context = fixture.CreateContext();
        var repository = new MeasurementUnitRepository(context);

        var index = await repository.ListMatchIndexAsync(TestContext.Current.CancellationToken);
        var lookup = index.ToLookup(entry => entry.NormalizedText, StringComparer.Ordinal);

        var result = UnitMatcher.Resolve("furlongs", lookup);

        Assert.Null(result.Resolved);
        Assert.False(result.IsAmbiguous);
    }
}
