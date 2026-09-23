using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Seeding;
using CreatorPantry.Domain.Modules.Vocabulary.Seeding;
using CreatorPantry.Domain.Modules.Ingredients.Seeding;
using CreatorPantry.Domain.Managers.Paging;

namespace CreatorPantry.Tests.Reference;

/// <summary>
/// The conversion rules recipes.md requires, in the one place they are executable rather than documented.
/// </summary>
/// <remarks>
/// Phase 4 performs no conversions — this is the data that a later phase will convert <em>from</em>, and the
/// gate it must pass through. The rule these pin is the one the schema cannot express: mass, volume and count
/// are kept apart by a column and a check constraint, but two count nouns share a dimension <em>and</em> a
/// base factor of 1, so nothing structural stops <c>1 bunch = 1 clove</c>.
/// </remarks>
public sealed class MeasurementConversionPolicyTests
{
    [Theory]
    [InlineData(MeasurementDimension.Mass, "g", MeasurementDimension.Volume, "ml")]
    [InlineData(MeasurementDimension.Volume, "cup-us", MeasurementDimension.Mass, "g")]
    [InlineData(MeasurementDimension.Mass, "g", MeasurementDimension.Count, "each")]
    [InlineData(MeasurementDimension.Volume, "ml", MeasurementDimension.Temperature, "celsius")]
    [InlineData(MeasurementDimension.Count, "each", MeasurementDimension.Qualitative, "pinch")]
    public void Nothing_converts_across_dimensions(
        MeasurementDimension from, string fromCode, MeasurementDimension to, string toCode) =>
        Assert.False(MeasurementPolicy.MayConvert(from, fromCode, to, toCode));

    [Theory]
    [InlineData("g", "kg")]
    [InlineData("kg", "lb")]
    [InlineData("ml", "cup-us")]
    [InlineData("tsp", "tbsp")]
    public void Mass_and_volume_convert_within_their_own_dimension(string fromCode, string toCode)
    {
        var from = Unit(fromCode);
        var to = Unit(toCode);

        Assert.True(MeasurementPolicy.MayConvert(from.Dimension, from.Code, to.Dimension, to.Code));
    }

    /// <summary>
    /// The finding this method exists for. Every count unit carries factor 1, so the arithmetic is available
    /// and wrong: one bunch is not one clove, and a head of garlic is not a single clove of it.
    /// </summary>
    [Theory]
    [InlineData("clove", "each")]
    [InlineData("bunch", "clove")]
    [InlineData("slice", "stick")]
    [InlineData("sprig", "bunch")]
    public void Two_different_count_nouns_never_convert(string fromCode, string toCode)
    {
        Assert.True(
            Unit(fromCode).Dimension == MeasurementDimension.Count
                && Unit(toCode).Dimension == MeasurementDimension.Count,
            "this test is only meaningful while both units are Count units");

        Assert.False(MeasurementPolicy.MayConvert(
            MeasurementDimension.Count, fromCode, MeasurementDimension.Count, toCode));
    }

    [Theory]
    [InlineData("clove")]
    [InlineData("each")]
    [InlineData("pinch")]
    [InlineData("celsius")]
    public void A_unit_always_converts_to_itself(string code)
    {
        var unit = Unit(code);

        Assert.True(MeasurementPolicy.MayConvert(unit.Dimension, unit.Code, unit.Dimension, unit.Code));
    }

    /// <summary>"A pinch" has no numeric relationship to a dash, or to anything else.</summary>
    [Theory]
    [InlineData("pinch", "dash")]
    [InlineData("splash", "to-taste")]
    public void Qualitative_units_never_convert_to_each_other(string fromCode, string toCode) =>
        Assert.False(MeasurementPolicy.MayConvert(
            MeasurementDimension.Qualitative, fromCode, MeasurementDimension.Qualitative, toCode));

    // --- The seeded shape these rules depend on ----------------------------------------------------------

    /// <summary>
    /// Pins the hazard itself. This fails the day someone seeds <c>dozen = 12</c>, which would make
    /// <c>1 dozen → 12 cloves</c> arithmetically "correct" — a numeric count unit needs an explicit allowlist
    /// in <see cref="MeasurementPolicy.MayConvert"/>, not a factor that quietly works.
    /// </summary>
    [Fact]
    public void Every_seeded_count_unit_has_a_factor_of_exactly_one() =>
        Assert.All(
            MeasurementSeedData.Units().Where(unit => unit.Dimension == MeasurementDimension.Count),
            unit => Assert.Equal(1m, unit.BaseUnitFactor));

    /// <summary>Temperature is affine; a multiplicative path must not exist for it at all.</summary>
    [Fact]
    public void No_seeded_temperature_or_qualitative_unit_has_a_factor() =>
        Assert.All(
            MeasurementSeedData.Units().Where(unit =>
                unit.Dimension is MeasurementDimension.Temperature or MeasurementDimension.Qualitative),
            unit => Assert.Null(unit.BaseUnitFactor));

    /// <summary>
    /// The seeded factors are exact definitions, not rounded ones. A value silently truncated to the EF
    /// default <c>decimal(18,2)</c> would make a US teaspoon 4.93 ml and put a 0.02% error into every scaled
    /// recipe the product ever produces.
    /// </summary>
    [Theory]
    [InlineData("lb", "453.59237")]
    [InlineData("oz", "28.349523125")]
    [InlineData("tsp", "4.92892159375")]
    [InlineData("tbsp", "14.78676478125")]
    [InlineData("cup-us", "236.5882365")]
    [InlineData("gallon-us", "3785.411784")]
    public void The_definitional_factors_are_seeded_exactly(string code, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Unit(code).BaseUnitFactor);

    private static Domain.Modules.Measurement.Data.Entities.MeasurementUnit Unit(string code) =>
        MeasurementSeedData.Units().Single(unit => unit.Code == code);
}
