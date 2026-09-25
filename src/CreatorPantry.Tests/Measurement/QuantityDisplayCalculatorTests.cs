using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Managers;

namespace CreatorPantry.Tests.Measurement;

/// <summary>
/// Golden tests for <see cref="QuantityDisplayCalculator"/>: common kitchen fractions, tiny/large values,
/// ranges, count units, and a metric plus a US-customary unit's presentation.
/// </summary>
public sealed class QuantityDisplayCalculatorTests
{
    private static readonly MeasurementUnitServiceModel CupUs = new(
        Guid.NewGuid(), "cup-us", "cup", "cups", "c", MeasurementDimension.Volume, MeasurementSystem.UsCustomary, 236.5882365m, DisplayPrecision: 2);

    private static readonly MeasurementUnitServiceModel Gram = new(
        Guid.NewGuid(), "g", "gram", "grams", "g", MeasurementDimension.Mass, MeasurementSystem.Metric, 1m, DisplayPrecision: 0);

    private static readonly MeasurementUnitServiceModel Clove = new(
        Guid.NewGuid(), "clove", "clove", "cloves", "clove", MeasurementDimension.Count, MeasurementSystem.Neutral, 1m, DisplayPrecision: 0);

    private static string Format(Quantity value, Quantity? upper, MeasurementUnitServiceModel unit, int precision, bool abbreviate = false) =>
        QuantityDisplayCalculator.Format(value, upper, unit, precision, abbreviate).Text;

    // ---- Common kitchen fractions ----

    [Theory]
    [InlineData(1, 2, "½ cups")]
    [InlineData(1, 3, "⅓ cups")]
    [InlineData(2, 3, "⅔ cups")]
    [InlineData(1, 4, "¼ cups")]
    [InlineData(3, 4, "¾ cups")]
    [InlineData(1, 8, "⅛ cups")]
    [InlineData(3, 8, "⅜ cups")]
    [InlineData(5, 8, "⅝ cups")]
    [InlineData(7, 8, "⅞ cups")]
    public void A_pure_kitchen_fraction_renders_as_its_glyph_with_no_leading_zero(long numerator, long denominator, string expected)
    {
        var text = Format(Quantity.FromFraction(numerator, denominator), null, CupUs, precision: 2);

        Assert.Equal(expected, text);
    }

    [Fact]
    public void A_whole_number_plus_a_kitchen_fraction_renders_as_a_mixed_number()
    {
        var text = Format(Quantity.FromFraction(3, 2), null, CupUs, precision: 2);

        Assert.Equal("1½ cups", text);
    }

    [Fact]
    public void A_singular_amount_uses_the_singular_unit_name()
    {
        var text = Format(Quantity.FromInt(1), null, CupUs, precision: 2);

        Assert.Equal("1 cup", text);
    }

    [Fact]
    public void A_reported_result_names_the_kitchen_fraction_presentation()
    {
        var result = QuantityDisplayCalculator.Format(Quantity.FromFraction(1, 2), null, CupUs, precision: 2);

        Assert.Equal(FractionPresentation.Fraction, result.Presentation);
    }

    // ---- Tiny and large values ----

    [Fact]
    public void A_tiny_value_with_no_matching_kitchen_fraction_falls_back_to_a_rounded_decimal()
    {
        // 1/64 matches no denominator in {2,3,4,8}.
        var result = QuantityDisplayCalculator.Format(Quantity.FromFraction(1, 64), null, CupUs, precision: 2);

        Assert.Equal("0.02 cups", result.Text);
        Assert.Equal(FractionPresentation.Decimal, result.Presentation);
    }

    [Fact]
    public void A_large_whole_value_renders_with_no_decimal_point_or_fraction_glyph()
    {
        var text = Format(Quantity.FromInt(1000), null, Gram, precision: 0);

        Assert.Equal("1000 grams", text);
    }

    [Fact]
    public void A_large_value_with_a_kitchen_fraction_remainder_still_gets_the_glyph()
    {
        var text = Format(Quantity.FromFraction(2469, 2), null, Gram, precision: 0);

        Assert.Equal("1234½ grams", text);
    }

    [Fact]
    public void A_large_value_with_no_matching_fraction_falls_back_to_a_rounded_decimal()
    {
        var result = QuantityDisplayCalculator.Format(Quantity.FromFraction(10001, 7), null, Gram, precision: 2);

        Assert.Equal(FractionPresentation.Decimal, result.Presentation);
        Assert.Equal("1428.71 grams", result.Text);
    }

    // ---- Ranges ----

    [Fact]
    public void A_range_joins_both_bounds_with_one_shared_unit()
    {
        var text = Format(Quantity.FromInt(2), Quantity.FromInt(3), CupUs, precision: 2);

        Assert.Equal("2–3 cups", text);
    }

    [Fact]
    public void A_range_is_always_plural_even_when_its_lower_bound_alone_would_be_singular()
    {
        var text = Format(Quantity.FromInt(1), Quantity.FromFraction(3, 2), CupUs, precision: 2);

        Assert.Equal("1–1½ cups", text);
    }

    [Fact]
    public void A_range_reports_each_bounds_own_presentation_independently()
    {
        var result = QuantityDisplayCalculator.Format(Quantity.FromFraction(1, 2), Quantity.FromFraction(1, 64), CupUs, precision: 2);

        Assert.Equal(FractionPresentation.Fraction, result.Presentation);
        Assert.Equal(FractionPresentation.Decimal, result.UpperPresentation);
    }

    [Fact]
    public void A_single_value_has_no_upper_presentation()
    {
        var result = QuantityDisplayCalculator.Format(Quantity.FromInt(2), null, CupUs, precision: 2);

        Assert.Null(result.UpperPresentation);
    }

    // ---- Count units ----

    [Fact]
    public void A_single_count_unit_uses_the_singular_form_with_no_decimal_artifact()
    {
        var text = Format(Quantity.FromInt(1), null, Clove, precision: 0);

        Assert.Equal("1 clove", text);
    }

    [Fact]
    public void Multiple_whole_count_units_use_the_plural_form_with_no_decimal_artifact()
    {
        var text = Format(Quantity.FromInt(3), null, Clove, precision: 0);

        Assert.Equal("3 cloves", text);
    }

    [Fact]
    public void A_fractional_count_still_renders_as_a_kitchen_fraction_when_it_matches_one()
    {
        var text = Format(Quantity.FromFraction(1, 2), null, Clove, precision: 0);

        Assert.Equal("½ cloves", text);
    }

    // ---- Configured metric and US presentation ----

    [Fact]
    public void A_metric_unit_renders_its_full_plural_name_by_default()
    {
        var text = Format(Quantity.FromInt(500), null, Gram, precision: 0);

        Assert.Equal("500 grams", text);
    }

    [Fact]
    public void A_metric_unit_renders_its_abbreviation_when_requested()
    {
        var text = Format(Quantity.FromInt(500), null, Gram, precision: 0, abbreviate: true);

        Assert.Equal("500 g", text);
    }

    [Fact]
    public void A_us_customary_unit_renders_its_full_plural_name_by_default()
    {
        var text = Format(Quantity.FromInt(2), null, CupUs, precision: 2);

        Assert.Equal("2 cups", text);
    }

    [Fact]
    public void A_us_customary_unit_renders_its_abbreviation_when_requested()
    {
        var text = Format(Quantity.FromInt(2), null, CupUs, precision: 2, abbreviate: true);

        Assert.Equal("2 c", text);
    }

    // ---- Precision and rounding are echoed, never compounded ----

    [Fact]
    public void The_result_echoes_the_precision_and_rounding_it_used()
    {
        var result = QuantityDisplayCalculator.Format(Quantity.FromFraction(1, 64), null, CupUs, precision: 3, rounding: MidpointRounding.AwayFromZero);

        Assert.Equal(3, result.Precision);
        Assert.Equal(MidpointRounding.AwayFromZero, result.Rounding);
        Assert.Equal("0.016 cups", result.Text);
    }

    [Fact]
    public void Formatting_never_changes_the_canonical_quantity_it_was_given()
    {
        var original = Quantity.FromFraction(1, 3);

        QuantityDisplayCalculator.Format(original, null, CupUs, precision: 0);

        Assert.Equal(Quantity.FromFraction(1, 3), original);
    }
}
