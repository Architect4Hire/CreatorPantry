using System.Numerics;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Quantities;
using CsCheck;

namespace CreatorPantry.Tests.Quantities;

/// <summary>
/// Property tests for <see cref="Quantity"/>: round trips through the JSON contract and through
/// <see cref="decimal"/>, fraction simplification, extreme magnitudes, and invalid denominators.
/// </summary>
public sealed class QuantityTests
{
    private static readonly Gen<BigInteger> AnyInteger =
        Gen.Long[-1_000_000_000_000L, 1_000_000_000_000L].Select(l => (BigInteger)l);

    private static readonly Gen<BigInteger> PositiveInteger =
        Gen.Long[1L, 1_000_000_000_000L].Select(l => (BigInteger)l);

    /// <summary>Numerators guaranteed to exceed decimal's ~7.9×10^28 range, to exercise <see cref="Quantity.ToDecimal"/>'s failure path.</summary>
    private static readonly Gen<BigInteger> ExtremeMagnitude =
        Gen.Select(Gen.Int[30, 60], Gen.Long[1, 1000], (exponent, offset) => BigInteger.Pow(10, exponent) + offset);

    private static int ScaleOf(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);

        return (bits[3] >> 16) & 0x7F;
    }

    // ---- Round trips ----

    [Fact]
    public void Round_trips_through_the_JSON_contract()
    {
        Gen.Select(AnyInteger, PositiveInteger).Sample((numerator, denominator) =>
        {
            var quantity = Quantity.FromFraction(numerator, denominator);
            var json = JsonSerializer.Serialize(quantity);
            var roundTripped = JsonSerializer.Deserialize<Quantity>(json);

            Assert.Equal(quantity, roundTripped);
        });
    }

    [Fact]
    public void A_decimal_round_trips_through_FromDecimal_and_ToDecimal_at_its_own_scale()
    {
        Gen.Decimal[-1_000_000m, 1_000_000m].Sample(value =>
        {
            var quantity = Quantity.FromDecimal(value);

            Assert.Equal(value, quantity.ToDecimal(ScaleOf(value)));
        });
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2.5")]
    [InlineData("-2.5")]
    [InlineData("4.92892159375")] // a US teaspoon in millilitres — the exact case QuantityFormat.cs documents
    [InlineData("0.00000000001")]
    public void Common_kitchen_decimals_round_trip_exactly(string text)
    {
        var value = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        var quantity = Quantity.FromDecimal(value);

        Assert.Equal(value, quantity.ToDecimal(ScaleOf(value)));
    }

    [Fact]
    public void Multiplying_up_and_back_down_by_the_same_factor_is_exact()
    {
        // The reason this type exists: 1/3 cup, tripled and thirded, is exactly 1/3 cup again — a decimal(28,12)
        // column cannot make that claim.
        Gen.Long[1, 1000].Select(l => (BigInteger)l).Sample(denominator =>
        {
            var third = Quantity.FromFraction(BigInteger.One, denominator);
            var upAndDown = third
                .Multiply(Quantity.FromFraction(denominator, BigInteger.One))
                .Multiply(Quantity.FromFraction(BigInteger.One, denominator));

            Assert.Equal(third, upAndDown);
        });
    }

    // ---- Simplification ----

    [Fact]
    public void The_reduced_form_is_always_in_lowest_terms()
    {
        Gen.Select(AnyInteger, PositiveInteger).Sample((numerator, denominator) =>
        {
            var quantity = Quantity.FromFraction(numerator, denominator);

            Assert.Equal(BigInteger.One, BigInteger.GreatestCommonDivisor(BigInteger.Abs(quantity.Numerator), quantity.Denominator));
        });
    }

    [Fact]
    public void Scaling_numerator_and_denominator_by_a_common_factor_does_not_change_the_value()
    {
        Gen.Select(AnyInteger, PositiveInteger, Gen.Long[1, 1000].Select(l => (BigInteger)l))
            .Sample((numerator, denominator, factor) =>
            {
                var original = Quantity.FromFraction(numerator, denominator);
                var scaled = Quantity.FromFraction(numerator * factor, denominator * factor);

                Assert.Equal(original, scaled);
            });
    }

    // ---- Extremes ----

    [Fact]
    public void An_extremely_large_numerator_still_reduces_and_compares_correctly()
    {
        Gen.Select(ExtremeMagnitude, PositiveInteger).Sample((numerator, denominator) =>
        {
            var quantity = Quantity.FromFraction(numerator, denominator);

            Assert.Equal(BigInteger.One, BigInteger.GreatestCommonDivisor(BigInteger.Abs(quantity.Numerator), quantity.Denominator));
            Assert.True(quantity > Quantity.Zero);
        });
    }

    [Fact]
    public void A_quantity_too_large_for_decimal_throws_instead_of_silently_truncating()
    {
        ExtremeMagnitude.Sample(numerator =>
        {
            var quantity = Quantity.FromFraction(numerator, BigInteger.One);

            Assert.Throws<OverflowException>(() => quantity.ToDecimal(0));
        });
    }

    // ---- Invalid denominators ----

    [Fact]
    public void A_zero_denominator_is_rejected()
    {
        AnyInteger.Sample(numerator =>
            Assert.Throws<ArgumentException>(() => Quantity.FromFraction(numerator, BigInteger.Zero)));
    }

    [Fact]
    public void A_negative_denominator_is_normalized_not_rejected()
    {
        Gen.Select(AnyInteger, PositiveInteger).Sample((numerator, denominator) =>
        {
            var fromNegativeDenominator = Quantity.FromFraction(numerator, -denominator);
            var fromNegatedNumerator = Quantity.FromFraction(-numerator, denominator);

            Assert.Equal(fromNegatedNumerator, fromNegativeDenominator);
            Assert.True(fromNegativeDenominator.Denominator > 0);
        });
    }

    // ---- Ordering ----

    [Fact]
    public void CompareTo_agrees_with_cross_multiplication()
    {
        Gen.Select(AnyInteger, PositiveInteger, AnyInteger, PositiveInteger)
            .Sample((n1, d1, n2, d2) =>
            {
                var left = Quantity.FromFraction(n1, d1);
                var right = Quantity.FromFraction(n2, d2);

                var expectedSign = Math.Sign((n1 * d2).CompareTo(n2 * d1));
                var actualSign = Math.Sign(left.CompareTo(right));

                Assert.Equal(expectedSign, actualSign);
            });
    }
}
