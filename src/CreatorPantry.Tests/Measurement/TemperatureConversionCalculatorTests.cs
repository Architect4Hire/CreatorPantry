using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CsCheck;

namespace CreatorPantry.Tests.Measurement;

/// <summary>
/// Exact formula, conventional display rounding, and unknown-context preservation tests for
/// <see cref="TemperatureConversionCalculator"/>.
/// </summary>
public sealed class TemperatureConversionCalculatorTests
{
    private static readonly Gen<decimal> AnyTemperature =
        Gen.Int[-100_00, 500_00].Select(i => i / 100m);

    private static readonly Gen<string?> AnyContext =
        Gen.OneOf(Gen.Const((string?)null), Gen.String[1, 40].Select(s => (string?)s));

    // ---- Exact formula ----

    [Theory]
    [InlineData(180, 356)]
    [InlineData(200, 392)]
    [InlineData(0, 32)]
    [InlineData(100, 212)]
    public void Celsius_to_fahrenheit_matches_the_exact_formula(decimal celsius, decimal expectedFahrenheit)
    {
        var result = TemperatureConversionCalculator.Convert(celsius, TemperatureScale.Celsius, TemperatureScale.Fahrenheit, precision: 0);

        Assert.Equal(Quantity.FromDecimal(expectedFahrenheit), result.ConvertedValue);
        Assert.Equal(expectedFahrenheit, result.ConvertedDisplayValue);
        Assert.Equal("°F = °C × 9/5 + 32", result.Formula);
    }

    [Theory]
    [InlineData(32, 0)]
    [InlineData(212, 100)]
    public void Fahrenheit_to_celsius_matches_the_exact_formula(decimal fahrenheit, decimal expectedCelsius)
    {
        var result = TemperatureConversionCalculator.Convert(fahrenheit, TemperatureScale.Fahrenheit, TemperatureScale.Celsius, precision: 0);

        Assert.Equal(Quantity.FromDecimal(expectedCelsius), result.ConvertedValue);
        Assert.Equal("°C = (°F − 32) × 5/9", result.Formula);
    }

    [Fact]
    public void Fahrenheit_to_celsius_is_exact_even_when_the_decimal_display_would_repeat()
    {
        // 350 °F is not a clean Celsius figure: (350 - 32) × 5/9 = 1590/9, which never terminates in decimal.
        var result = TemperatureConversionCalculator.Convert(350m, TemperatureScale.Fahrenheit, TemperatureScale.Celsius, precision: 4);

        Assert.Equal(Quantity.FromFraction(1590, 9), result.ConvertedValue);
        Assert.Equal(176.6667m, result.ConvertedDisplayValue);
    }

    [Fact]
    public void Converting_to_the_same_scale_is_an_exact_identity()
    {
        AnyTemperature.Sample(value =>
        {
            var result = TemperatureConversionCalculator.Convert(value, TemperatureScale.Celsius, TemperatureScale.Celsius, precision: 2);

            Assert.Equal(Quantity.FromDecimal(value), result.ConvertedValue);
            Assert.Equal("identity — source and target scale are the same", result.Formula);
        });
    }

    [Fact]
    public void Converting_there_and_back_returns_the_exact_original_value()
    {
        AnyTemperature.Sample(value =>
        {
            // ×9/5 always terminates in decimal for a finite decimal input (its reduced denominator is a power
            // of 5), so scale 6 captures the Fahrenheit leg losslessly — the round trip below is then exact
            // all the way through, not merely close.
            var there = TemperatureConversionCalculator.Convert(value, TemperatureScale.Celsius, TemperatureScale.Fahrenheit, precision: 6);
            var back = TemperatureConversionCalculator.Convert(
                there.ConvertedValue.ToDecimal(6), TemperatureScale.Fahrenheit, TemperatureScale.Celsius, precision: 6);

            Assert.Equal(Quantity.FromDecimal(value), back.ConvertedValue);
        });
    }

    // ---- Conventional display rounding ----

    [Fact]
    public void The_display_value_is_rounded_once_to_the_caller_supplied_precision()
    {
        Gen.Select(AnyTemperature, Gen.Int[0, 6]).Sample((value, precision) =>
        {
            var result = TemperatureConversionCalculator.Convert(value, TemperatureScale.Fahrenheit, TemperatureScale.Celsius, precision);

            Assert.Equal(result.ConvertedValue.ToDecimal(precision), result.ConvertedDisplayValue);
            Assert.Equal(precision, result.Precision);
            Assert.Equal(MidpointRounding.ToEven, result.Rounding);
        });
    }

    [Fact]
    public void A_conventional_oven_conversion_rounds_to_whole_degrees()
    {
        // 163 °C is not a clean Fahrenheit figure: 163 × 9/5 + 32 = 325.4.
        var result = TemperatureConversionCalculator.Convert(163m, TemperatureScale.Celsius, TemperatureScale.Fahrenheit, precision: 0);

        Assert.Equal(325m, result.ConvertedDisplayValue);
    }

    // ---- Unknown-context preservation ----

    [Fact]
    public void Oven_mode_context_and_the_safety_note_pass_through_untouched()
    {
        Gen.Select(AnyTemperature, AnyContext, AnyContext).Sample((value, ovenMode, safetyNote) =>
        {
            var result = TemperatureConversionCalculator.Convert(
                value, TemperatureScale.Celsius, TemperatureScale.Fahrenheit, precision: 1, ovenMode, safetyNote);

            Assert.Equal(ovenMode, result.OvenModeContext);
            Assert.Equal(safetyNote, result.SafetyNote);
        });
    }

    [Fact]
    public void No_context_supplied_stays_null_rather_than_being_synthesized()
    {
        var result = TemperatureConversionCalculator.Convert(180m, TemperatureScale.Celsius, TemperatureScale.Fahrenheit, precision: 0);

        Assert.Null(result.OvenModeContext);
        Assert.Null(result.SafetyNote);
    }

    [Fact]
    public void The_source_value_and_scale_are_echoed_exactly_regardless_of_conversion()
    {
        AnyTemperature.Sample(value =>
        {
            var result = TemperatureConversionCalculator.Convert(value, TemperatureScale.Fahrenheit, TemperatureScale.Celsius, precision: 2);

            Assert.Equal(Quantity.FromDecimal(value), result.SourceValue);
            Assert.Equal(TemperatureScale.Fahrenheit, result.SourceScale);
            Assert.Equal(TemperatureScale.Celsius, result.ConvertedScale);
        });
    }
}
