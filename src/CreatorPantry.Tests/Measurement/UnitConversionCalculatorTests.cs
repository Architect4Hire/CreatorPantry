using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CsCheck;

namespace CreatorPantry.Tests.Measurement;

/// <summary>
/// Property tests and a checked-in fixture set for <see cref="UnitConversionCalculator"/>: round trips,
/// incompatible units, missing density, source attribution, and display precision.
/// </summary>
public sealed class UnitConversionCalculatorTests
{
    // Seeded factors from MeasurementSeedData, so fixtures are checkable against the real catalogue.
    private static readonly MeasurementUnitServiceModel Gram = Unit("g", MeasurementDimension.Mass, 1m);
    private static readonly MeasurementUnitServiceModel Kilogram = Unit("kg", MeasurementDimension.Mass, 1000m);
    private static readonly MeasurementUnitServiceModel Milliliter = Unit("ml", MeasurementDimension.Volume, 1m);
    private static readonly MeasurementUnitServiceModel Teaspoon = Unit("tsp", MeasurementDimension.Volume, 4.92892159375m);
    private static readonly MeasurementUnitServiceModel Tablespoon = Unit("tbsp", MeasurementDimension.Volume, 14.78676478125m);
    private static readonly MeasurementUnitServiceModel CupUs = Unit("cup-us", MeasurementDimension.Volume, 236.5882365m);
    private static readonly MeasurementUnitServiceModel Each = Unit("each", MeasurementDimension.Count, 1m);
    private static readonly MeasurementUnitServiceModel Clove = Unit("clove", MeasurementDimension.Count, 1m);
    private static readonly MeasurementUnitServiceModel Bunch = Unit("bunch", MeasurementDimension.Count, 1m);
    private static readonly MeasurementUnitServiceModel Celsius = Unit("celsius", MeasurementDimension.Temperature, null);
    private static readonly MeasurementUnitServiceModel Fahrenheit = Unit("fahrenheit", MeasurementDimension.Temperature, null);
    private static readonly MeasurementUnitServiceModel Pinch = Unit("pinch", MeasurementDimension.Qualitative, null);
    private static readonly MeasurementUnitServiceModel ToTaste = Unit("to-taste", MeasurementDimension.Qualitative, null);

    private static MeasurementUnitServiceModel Unit(string code, MeasurementDimension dimension, decimal? baseUnitFactor) =>
        new(Guid.NewGuid(), code, code, code, code, dimension, MeasurementSystem.Neutral, baseUnitFactor, DisplayPrecision: 2);

    private static readonly Gen<Quantity> PositiveCentsQuantity =
        Gen.Int[1, 1_000_00].Select(i => Quantity.FromFraction(i, 100));

    private static UnitConversionResult ConvertOrThrow(
        Quantity quantity, MeasurementUnitServiceModel from, MeasurementUnitServiceModel to, IngredientDensityConversionInput? density = null)
    {
        var outcome = UnitConversionCalculator.Convert(quantity, from, to, density);

        Assert.True(outcome.Succeeded);
        return outcome.Result!;
    }

    // ---- Round trips ----

    [Fact]
    public void Same_dimension_conversion_round_trips_exactly()
    {
        PositiveCentsQuantity.Sample(quantity =>
        {
            var there = ConvertOrThrow(quantity, Teaspoon, CupUs);
            var back = ConvertOrThrow(there.ConvertedQuantity, CupUs, Teaspoon);

            Assert.Equal(quantity, back.ConvertedQuantity);
        });
    }

    [Fact]
    public void Density_bridged_conversion_round_trips_exactly()
    {
        var density = new IngredientDensityConversionInput(120m, Gram, 1m, CupUs, "Test source");

        PositiveCentsQuantity.Sample(quantity =>
        {
            var toMass = ConvertOrThrow(quantity, CupUs, Gram, density);
            var back = ConvertOrThrow(toMass.ConvertedQuantity, Gram, CupUs, density);

            Assert.Equal(quantity, back.ConvertedQuantity);
        });
    }

    [Fact]
    public void Three_teaspoons_is_exactly_one_tablespoon()
    {
        var result = ConvertOrThrow(Quantity.FromInt(3), Teaspoon, Tablespoon);

        Assert.Equal(Quantity.FromInt(1), result.ConvertedQuantity);
        Assert.Equal(1m, result.ConvertedDisplayQuantity);
        Assert.Equal(UnitConversionMethod.SameDimensionFactor, result.Method);
        Assert.Null(result.Source);
    }

    [Fact]
    public void A_unit_converts_to_itself_as_the_identity()
    {
        PositiveCentsQuantity.Sample(quantity =>
        {
            var result = ConvertOrThrow(quantity, Kilogram, Kilogram);

            Assert.Equal(quantity, result.ConvertedQuantity);
        });
    }

    // ---- Density fixture ----

    [Fact]
    public void Two_cups_of_flour_at_120_grams_per_cup_converts_to_240_grams_exactly()
    {
        var density = new IngredientDensityConversionInput(120m, Gram, 1m, CupUs, "Internal test density");

        var result = ConvertOrThrow(Quantity.FromInt(2), CupUs, Gram, density);

        Assert.Equal(Quantity.FromInt(240), result.ConvertedQuantity);
        Assert.Equal(240m, result.ConvertedDisplayQuantity);
        Assert.Equal(UnitConversionMethod.IngredientDensity, result.Method);
    }

    // ---- Source ----

    [Fact]
    public void A_density_bridged_result_echoes_the_density_source_citation()
    {
        var density = new IngredientDensityConversionInput(120m, Gram, 1m, CupUs, "USDA FoodData Central");

        var result = ConvertOrThrow(Quantity.FromInt(1), CupUs, Gram, density);

        Assert.Equal("USDA FoodData Central", result.Source);
    }

    [Fact]
    public void A_same_dimension_result_has_no_source()
    {
        var result = ConvertOrThrow(Quantity.FromInt(1), Teaspoon, Tablespoon);

        Assert.Null(result.Source);
    }

    // ---- Precision and rounding ----

    [Fact]
    public void The_display_quantity_is_rounded_once_to_the_target_units_precision()
    {
        var oneThirdCup = new MeasurementUnitServiceModel(Guid.NewGuid(), "third-cup", "third-cup", "third-cup", "third-cup",
            MeasurementDimension.Volume, MeasurementSystem.Neutral, CupUs.BaseUnitFactor, DisplayPrecision: 3);

        var result = ConvertOrThrow(Quantity.FromInt(1), CupUs, oneThirdCup);

        Assert.Equal(3, result.Precision);
        Assert.Equal(result.ConvertedQuantity.ToDecimal(3), result.ConvertedDisplayQuantity);
        Assert.Equal(MidpointRounding.ToEven, result.Rounding);
    }

    // ---- Incompatible units ----

    [Fact]
    public void Different_count_nouns_do_not_convert()
    {
        var outcome = UnitConversionCalculator.Convert(Quantity.FromInt(1), Clove, Bunch);

        Assert.False(outcome.Succeeded);
        Assert.Equal(UnitConversionError.IncompatibleUnits, outcome.Error);
    }

    [Fact]
    public void The_same_count_noun_converts_as_the_identity()
    {
        var result = ConvertOrThrow(Quantity.FromInt(3), Clove, Clove);

        Assert.Equal(Quantity.FromInt(3), result.ConvertedQuantity);
    }

    [Fact]
    public void Different_qualitative_phrases_do_not_convert()
    {
        var outcome = UnitConversionCalculator.Convert(Quantity.FromInt(1), Pinch, ToTaste);

        Assert.False(outcome.Succeeded);
        Assert.Equal(UnitConversionError.IncompatibleUnits, outcome.Error);
    }

    [Fact]
    public void The_same_qualitative_phrase_converts_as_the_identity_with_no_arithmetic()
    {
        var result = ConvertOrThrow(Quantity.FromInt(1), Pinch, Pinch);

        Assert.Equal(Quantity.FromInt(1), result.ConvertedQuantity);
    }

    [Fact]
    public void A_non_mass_volume_cross_dimension_pair_does_not_convert()
    {
        var outcome = UnitConversionCalculator.Convert(Quantity.FromInt(2), Each, Gram);

        Assert.False(outcome.Succeeded);
        Assert.Equal(UnitConversionError.IncompatibleUnits, outcome.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Temperature_is_never_converted_by_this_calculator(bool fromIsTemperature)
    {
        var (from, to) = fromIsTemperature ? (Celsius, Gram) : (Gram, Fahrenheit);

        var outcome = UnitConversionCalculator.Convert(Quantity.FromInt(100), from, to);

        Assert.False(outcome.Succeeded);
        Assert.Equal(UnitConversionError.TemperatureNotSupported, outcome.Error);
    }

    // ---- Missing density ----

    [Fact]
    public void A_mass_volume_pair_without_a_density_fact_is_reported_as_unavailable_rather_than_guessed()
    {
        var outcome = UnitConversionCalculator.Convert(Quantity.FromInt(1), CupUs, Gram, density: null);

        Assert.False(outcome.Succeeded);
        Assert.Equal(UnitConversionError.MissingDensity, outcome.Error);
    }
}
