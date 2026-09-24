using CreatorPantry.Domain.Modules.Measurement.Managers;

namespace CreatorPantry.Tests.Measurement;

/// <summary>
/// <see cref="UnitMatcher"/> against a hand-built index, covering ambiguity, alias, punctuation, plural, and
/// no-match — the categories 7.3's prompt requires.
/// </summary>
/// <remarks>
/// Unlike the ingredient catalogue, a real cross-unit tie needs no synthetic construction here:
/// <c>MeasurementPolicy.NormalizeAlias</c> lowercases everything, so the case-ambiguous single-letter
/// abbreviations "T" (tablespoon) and "t" (teaspoon) — deliberately excluded from the seeded alias set for
/// exactly this reason, per that method's own remarks — are reused below as the ambiguity fixture.
/// </remarks>
public sealed class UnitMatcherTests
{
    private static readonly Guid TeaspoonId = Guid.NewGuid();
    private static readonly Guid TablespoonId = Guid.NewGuid();
    private static readonly Guid GramId = Guid.NewGuid();
    private static readonly Guid FluidOunceId = Guid.NewGuid();

    private static readonly ILookup<string, UnitMatchIndexEntry> Index = new UnitMatchIndexEntry[]
    {
        new(TeaspoonId, "teaspoon", "tsp", UnitMatchKind.Code),
        new(TeaspoonId, "teaspoon", "teaspoon", UnitMatchKind.DisplayName),
        new(TeaspoonId, "teaspoon", "teaspoons", UnitMatchKind.PluralName),
        new(TeaspoonId, "teaspoon", "tsp", UnitMatchKind.Abbreviation),
        new(TeaspoonId, "teaspoon", "t", UnitMatchKind.Abbreviation),

        new(TablespoonId, "tablespoon", "tbsp", UnitMatchKind.Code),
        new(TablespoonId, "tablespoon", "t", UnitMatchKind.Abbreviation),

        new(GramId, "gram", "g", UnitMatchKind.Code),
        new(GramId, "gram", "grams", UnitMatchKind.PluralName),

        new(FluidOunceId, "fluid ounce", "floz", UnitMatchKind.Code),
        new(FluidOunceId, "fluid ounce", "floz", UnitMatchKind.Alias),
    }.ToLookup(entry => entry.NormalizedText, StringComparer.Ordinal);

    // ---- Exact and alias ----

    [Fact]
    public void A_code_resolves_with_no_alternates()
    {
        var result = UnitMatcher.Resolve("tbsp", Index);

        Assert.Equal(TablespoonId, result.Resolved!.MeasurementUnitId);
        Assert.Equal(UnitMatchKind.Code, result.Resolved.Kind);
        Assert.Empty(result.Alternates);
    }

    [Fact]
    public void An_alias_resolves_to_its_unit_even_when_a_code_also_matches_the_same_unit()
    {
        // "floz" is both fluid ounce's Code and its own UnitAlias row — one unit found two ways, not a tie.
        var result = UnitMatcher.Resolve("floz", Index);

        Assert.Equal(FluidOunceId, result.Resolved!.MeasurementUnitId);
        Assert.Equal(UnitMatchKind.Code, result.Resolved.Kind);
        Assert.Empty(result.Alternates);
        Assert.False(result.IsAmbiguous);
    }

    // ---- Punctuation ----

    [Theory]
    [InlineData("fl oz")]
    [InlineData("fl. oz.")]
    [InlineData("FL OZ")]
    [InlineData("floz")]
    public void Punctuation_and_case_collapse_to_the_same_normalized_form(string candidate)
    {
        var result = UnitMatcher.Resolve(candidate, Index);

        Assert.Equal(FluidOunceId, result.Resolved!.MeasurementUnitId);
    }

    // ---- Plural ----

    [Fact]
    public void A_plural_display_form_resolves()
    {
        var result = UnitMatcher.Resolve("teaspoons", Index);

        Assert.Equal(TeaspoonId, result.Resolved!.MeasurementUnitId);
        Assert.Equal(UnitMatchKind.PluralName, result.Resolved.Kind);
    }

    [Fact]
    public void Grams_resolves_through_its_plural_name()
    {
        var result = UnitMatcher.Resolve("grams", Index);

        Assert.Equal(GramId, result.Resolved!.MeasurementUnitId);
    }

    // ---- No match ----

    [Fact]
    public void An_unrecognized_candidate_is_unresolved_and_not_ambiguous()
    {
        var result = UnitMatcher.Resolve("banana", Index);

        Assert.Null(result.Resolved);
        Assert.False(result.IsAmbiguous);
        Assert.Empty(result.Alternates);
    }

    // ---- Ambiguity ----

    [Fact]
    public void The_case_ambiguous_single_letter_abbreviation_is_left_unresolved()
    {
        // "T" (tablespoon) and "t" (teaspoon) both lowercase to "t" — MeasurementPolicy.NormalizeAlias's own
        // remarks name this exact collision as the reason such single letters are kept out of the real seeded
        // alias set. The resolver must not guess between two different units tied at the same tier.
        var result = UnitMatcher.Resolve("t", Index);

        Assert.Null(result.Resolved);
        Assert.True(result.IsAmbiguous);

        // Ordered by display name ("tablespoon" before "teaspoon"), not by which happened to be indexed first.
        Assert.Equal(
            [TablespoonId, TeaspoonId],
            result.Alternates.Select(candidate => candidate.MeasurementUnitId));
    }
}
