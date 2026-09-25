using System.Globalization;
using System.Numerics;
using CreatorPantry.Domain.Managers.Quantities;

namespace CreatorPantry.Domain.Modules.Measurement.Managers;

/// <summary>
/// Renders a canonical <see cref="Quantity"/> — or a <see cref="Quantity"/> pair standing in for a
/// <see cref="QuantityRange"/> — as readable text: a common kitchen fraction glyph when the exact remainder
/// matches one, a rounded decimal otherwise, with the unit's singular, plural, or abbreviated form selected to
/// match (ING-006, CALC-001/006).
/// </summary>
/// <remarks>
/// <para>
/// <strong>No approximation, because nothing here is approximate to begin with.</strong>
/// <see cref="Quantity"/> is always held in lowest terms (7.1), so "does this value's fractional part match a
/// common kitchen fraction" is not a search or a tolerance check — it is simply asking whether the exact,
/// already-reduced remainder's own denominator is 2, 3, 4, or 8. A value that does not land on one of those
/// denominators was never close to a kitchen fraction to begin with; it gets a decimal, not a guess.
/// </para>
/// <para>
/// <strong>Presentation only.</strong> This never rounds before deriving the whole/remainder split — the split
/// itself is exact BigInteger arithmetic — and rounding happens at most once, for the decimal fallback alone.
/// Nothing here returns a new canonical quantity; recipes.md's ban on compounding prior rounding holds because
/// there is no prior rounding here to compound.
/// </para>
/// <para>
/// Pure and stateless, like <see cref="UnitConversionCalculator"/> (7.7): no persistence, no locale beyond the
/// invariant culture used for the digits themselves.
/// </para>
/// </remarks>
public static class QuantityDisplayCalculator
{
    // The complete set a Quantity's own reduction can ever produce for a remainder with one of these
    // denominators — e.g. 2/4 always reduces to 1/2 at construction, so 2/4 can never appear here.
    private static readonly IReadOnlyDictionary<(int Numerator, int Denominator), char> KitchenFractionGlyphs =
        new Dictionary<(int, int), char>
        {
            [(1, 2)] = '½',
            [(1, 3)] = '⅓',
            [(2, 3)] = '⅔',
            [(1, 4)] = '¼',
            [(3, 4)] = '¾',
            [(1, 8)] = '⅛',
            [(3, 8)] = '⅜',
            [(5, 8)] = '⅝',
            [(7, 8)] = '⅞',
        };

    /// <param name="value">The quantity, or a range's lower bound when <paramref name="upperValue"/> is given.</param>
    /// <param name="upperValue">A range's upper bound, or <see langword="null"/> for a single value.</param>
    /// <param name="unit">Supplies the display, plural, and abbreviated unit text to choose between.</param>
    /// <param name="precision">Fractional digits for the decimal fallback — the caller's own choice, not assumed here.</param>
    /// <param name="useAbbreviation">Renders <see cref="MeasurementUnitServiceModel.Abbreviation"/> instead of the spelled-out name.</param>
    /// <param name="rounding"><see cref="MidpointRounding.ToEven"/> (the default) or <see cref="MidpointRounding.AwayFromZero"/>.</param>
    public static QuantityDisplayResult Format(
        Quantity value,
        Quantity? upperValue,
        MeasurementUnitServiceModel unit,
        int precision,
        bool useAbbreviation = false,
        MidpointRounding rounding = MidpointRounding.ToEven)
    {
        ArgumentNullException.ThrowIfNull(unit);

        var lower = FormatNumber(value, precision, rounding);

        if (upperValue is not { } upper)
        {
            return new QuantityDisplayResult
            {
                Text = $"{lower.Text} {SelectUnitName(unit, lower.IsExactlyOne, useAbbreviation)}",
                Presentation = lower.Presentation,
                UpperPresentation = null,
                Precision = precision,
                Rounding = rounding,
            };
        }

        var upperFormatted = FormatNumber(upper, precision, rounding);

        // A range always spans more than one point (QuantityRange requires upper > lower strictly), so it is
        // never grammatically singular even when its lower bound alone would be.
        return new QuantityDisplayResult
        {
            Text = $"{lower.Text}–{upperFormatted.Text} {SelectUnitName(unit, isSingular: false, useAbbreviation)}",
            Presentation = lower.Presentation,
            UpperPresentation = upperFormatted.Presentation,
            Precision = precision,
            Rounding = rounding,
        };
    }

    private static (string Text, FractionPresentation Presentation, bool IsExactlyOne) FormatNumber(
        Quantity value, int precision, MidpointRounding rounding)
    {
        var whole = BigInteger.DivRem(value.Numerator, value.Denominator, out var remainderNumerator);
        var remainder = Quantity.FromFraction(remainderNumerator, value.Denominator);

        if (remainder.IsZero)
            return (whole.ToString(CultureInfo.InvariantCulture), FractionPresentation.Whole, whole == BigInteger.One);

        if (remainder.Denominator <= 8
            && KitchenFractionGlyphs.TryGetValue(((int)remainder.Numerator, (int)remainder.Denominator), out var glyph))
        {
            var text = whole.IsZero ? glyph.ToString() : $"{whole.ToString(CultureInfo.InvariantCulture)}{glyph}";
            return (text, FractionPresentation.Fraction, false);
        }

        var rounded = value.ToDecimal(precision, rounding);
        return (rounded.ToString(CultureInfo.InvariantCulture), FractionPresentation.Decimal, rounded == 1m);
    }

    private static string SelectUnitName(MeasurementUnitServiceModel unit, bool isSingular, bool useAbbreviation) =>
        useAbbreviation ? unit.Abbreviation : isSingular ? unit.DisplayName : unit.PluralName;
}
