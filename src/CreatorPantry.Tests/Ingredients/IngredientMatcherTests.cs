using CreatorPantry.Domain.Modules.Ingredients.Managers;

namespace CreatorPantry.Tests.Ingredients;

/// <summary>
/// <see cref="IngredientMatcher"/> against a hand-built index, covering ambiguity, alias, punctuation, plural,
/// and no-match — the categories 7.3's prompt requires.
/// </summary>
/// <remarks>
/// The index is built directly rather than through a database, because the property under test — what the
/// resolver does with a given set of hits — is independent of how those hits were produced, and a hand-built
/// index can construct the one scenario the real schema's unique indexes cannot (two different entities tied
/// at the very same precedence tier), which is exactly the case <see cref="IngredientMatcher"/> must call
/// ambiguous rather than guess at.
/// </remarks>
public sealed class IngredientMatcherTests
{
    private static readonly Guid FlourId = Guid.NewGuid();
    private static readonly Guid TomatoId = Guid.NewGuid();
    private static readonly Guid CilantroId = Guid.NewGuid();
    private static readonly Guid CorianderId = Guid.NewGuid();
    private static readonly Guid DeltaId = Guid.NewGuid();
    private static readonly Guid EchoId = Guid.NewGuid();

    private static readonly ILookup<string, IngredientMatchIndexEntry> Index = new IngredientMatchIndexEntry[]
    {
        new(FlourId, "All-Purpose Flour", "all purpose flour", IngredientMatchKind.CanonicalName),
        new(FlourId, "All-Purpose Flour", "flour", IngredientMatchKind.Alias),
        new(FlourId, "All-Purpose Flour", "plain flour", IngredientMatchKind.Alias),

        new(TomatoId, "Tomato", "tomato", IngredientMatchKind.CanonicalName),
        new(TomatoId, "Tomato", "tomatoes", IngredientMatchKind.Alias),

        // A canonical name colliding with a different ingredient's alias — the one cross-table collision the
        // schema's unique indexes cannot prevent (IngredientAlias.NormalizedAlias's own remarks).
        new(CilantroId, "Cilantro", "cilantro", IngredientMatchKind.CanonicalName),
        new(CorianderId, "Coriander", "cilantro", IngredientMatchKind.Alias),

        // Two different ingredients tied at the same precedence tier — synthetic, since no real seed data can
        // produce it, but the resolver must still refuse to guess between them.
        new(DeltaId, "Delta", "shared", IngredientMatchKind.Alias),
        new(EchoId, "Echo", "shared", IngredientMatchKind.Alias),
    }.ToLookup(entry => entry.NormalizedText, StringComparer.Ordinal);

    // ---- Exact and alias ----

    [Fact]
    public void An_exact_canonical_name_resolves_with_no_alternates()
    {
        var result = IngredientMatcher.Resolve("all purpose flour", Index);

        Assert.Equal(FlourId, result.Resolved!.IngredientId);
        Assert.Equal(IngredientMatchKind.CanonicalName, result.Resolved.Kind);
        Assert.Empty(result.Alternates);
        Assert.False(result.IsAmbiguous);
    }

    [Fact]
    public void An_alias_resolves_to_its_ingredient()
    {
        var result = IngredientMatcher.Resolve("flour", Index);

        Assert.Equal(FlourId, result.Resolved!.IngredientId);
        Assert.Equal(IngredientMatchKind.Alias, result.Resolved.Kind);
    }

    [Fact]
    public void A_second_unrelated_alias_of_the_same_ingredient_also_resolves()
    {
        var result = IngredientMatcher.Resolve("plain flour", Index);

        Assert.Equal(FlourId, result.Resolved!.IngredientId);
    }

    // ---- Punctuation ----

    [Theory]
    [InlineData("All-Purpose Flour")]
    [InlineData("all purpose flour")]
    [InlineData("ALL-PURPOSE FLOUR")]
    [InlineData("All-Purpose Flour  ")]
    public void Punctuation_case_and_spacing_do_not_change_the_match(string candidate)
    {
        var result = IngredientMatcher.Resolve(candidate, Index);

        Assert.Equal(FlourId, result.Resolved!.IngredientId);
    }

    // ---- Plural ----

    [Fact]
    public void A_plural_backed_by_an_alias_row_resolves()
    {
        var result = IngredientMatcher.Resolve("tomatoes", Index);

        Assert.Equal(TomatoId, result.Resolved!.IngredientId);
        Assert.Equal(IngredientMatchKind.Alias, result.Resolved.Kind);
    }

    [Fact]
    public void The_singular_form_resolves_through_the_canonical_name_instead()
    {
        var result = IngredientMatcher.Resolve("tomato", Index);

        Assert.Equal(TomatoId, result.Resolved!.IngredientId);
        Assert.Equal(IngredientMatchKind.CanonicalName, result.Resolved.Kind);
    }

    // ---- No match ----

    [Fact]
    public void An_unrecognized_candidate_is_unresolved_and_not_ambiguous()
    {
        var result = IngredientMatcher.Resolve("unobtainium", Index);

        Assert.Null(result.Resolved);
        Assert.False(result.IsAmbiguous);
        Assert.Empty(result.Alternates);
    }

    // ---- Ambiguity ----

    [Fact]
    public void A_canonical_name_wins_over_a_colliding_alias_but_the_alias_stays_visible()
    {
        // Not ambiguous: the codebase's own documented precedence (a canonical name always outranks an
        // alias) breaks this tie. The competing ingredient is not silently dropped, though — it stays
        // visible as a lower-ranked alternate.
        var result = IngredientMatcher.Resolve("cilantro", Index);

        Assert.False(result.IsAmbiguous);
        Assert.Equal(CilantroId, result.Resolved!.IngredientId);
        Assert.Equal(IngredientMatchKind.CanonicalName, result.Resolved.Kind);

        var alternate = Assert.Single(result.Alternates);
        Assert.Equal(CorianderId, alternate.IngredientId);
        Assert.Equal(IngredientMatchKind.Alias, alternate.Kind);
    }

    [Fact]
    public void Two_different_ingredients_tied_at_the_same_precedence_are_left_unresolved()
    {
        var result = IngredientMatcher.Resolve("shared", Index);

        Assert.Null(result.Resolved);
        Assert.True(result.IsAmbiguous);
        Assert.Equal([DeltaId, EchoId], result.Alternates.Select(candidate => candidate.IngredientId));
    }
}
