using System.Numerics;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Quantities;
using CsCheck;

namespace CreatorPantry.Tests.Quantities;

public sealed class QuantityRangeTests
{
    private static readonly Gen<BigInteger> SmallInteger =
        Gen.Long[-1_000_000L, 1_000_000L].Select(l => (BigInteger)l);

    private static readonly Gen<Quantity> AnyQuantity =
        Gen.Select(SmallInteger, Gen.Long[1L, 1_000_000L].Select(l => (BigInteger)l))
            .Select((numerator, denominator) => Quantity.FromFraction(numerator, denominator));

    // ---- Round trips ----

    [Fact]
    public void Round_trips_through_the_JSON_contract()
    {
        Gen.Select(AnyQuantity, Gen.Long[1, 1000].Select(l => (BigInteger)l))
            .Select((lower, gap) => (lower, upper: lower.Add(Quantity.FromFraction(gap, BigInteger.One))))
            .Sample(pair =>
            {
                var range = QuantityRange.Create(pair.lower, pair.upper);
                var json = JsonSerializer.Serialize(range);
                var roundTripped = JsonSerializer.Deserialize<QuantityRange>(json);

                Assert.Equal(range, roundTripped);
            });
    }

    // ---- Invalid bounds ----

    [Fact]
    public void An_upper_bound_that_does_not_exceed_the_lower_bound_is_rejected()
    {
        Gen.Select(AnyQuantity, Gen.Long[0, 1000].Select(l => (BigInteger)l))
            .Sample((lower, gap) =>
            {
                // upper <= lower for every non-negative gap subtracted from lower
                var upper = lower.Subtract(Quantity.FromFraction(gap, BigInteger.One));

                Assert.Throws<ArgumentException>(() => QuantityRange.Create(lower, upper));
            });
    }

    [Fact]
    public void Equal_bounds_are_rejected()
    {
        AnyQuantity.Sample(value => Assert.Throws<ArgumentException>(() => QuantityRange.Create(value, value)));
    }

    // ---- Scaling ----

    [Fact]
    public void Multiplying_by_a_positive_factor_preserves_order()
    {
        Gen.Select(SmallInteger, Gen.Long[1, 1000].Select(l => (BigInteger)l))
            .Sample((lowerValue, gap) =>
            {
                var lower = Quantity.FromFraction(lowerValue, BigInteger.One);
                var upper = lower.Add(Quantity.FromFraction(gap, BigInteger.One));
                var range = QuantityRange.Create(lower, upper);

                var scaled = range.Multiply(Quantity.FromFraction(3, 2));

                Assert.True(scaled.Lower < scaled.Upper);
                Assert.Equal(lower.Multiply(Quantity.FromFraction(3, 2)), scaled.Lower);
                Assert.Equal(upper.Multiply(Quantity.FromFraction(3, 2)), scaled.Upper);
            });
    }

    [Fact]
    public void Multiplying_by_a_negative_factor_swaps_the_bounds_to_keep_lower_below_upper()
    {
        Gen.Select(SmallInteger, Gen.Long[1, 1000].Select(l => (BigInteger)l))
            .Sample((lowerValue, gap) =>
            {
                var lower = Quantity.FromFraction(lowerValue, BigInteger.One);
                var upper = lower.Add(Quantity.FromFraction(gap, BigInteger.One));
                var range = QuantityRange.Create(lower, upper);

                var scaled = range.Multiply(Quantity.FromFraction(-1, 1));

                Assert.True(scaled.Lower < scaled.Upper);
                Assert.Equal(upper.Multiply(Quantity.FromFraction(-1, 1)), scaled.Lower);
                Assert.Equal(lower.Multiply(Quantity.FromFraction(-1, 1)), scaled.Upper);
            });
    }

    [Fact]
    public void Contains_is_true_for_both_bounds_and_anything_between()
    {
        var range = QuantityRange.Create(Quantity.FromInt(2), Quantity.FromInt(3));

        Assert.True(range.Contains(Quantity.FromInt(2)));
        Assert.True(range.Contains(Quantity.FromInt(3)));
        Assert.True(range.Contains(Quantity.FromFraction(5, 2)));
        Assert.False(range.Contains(Quantity.FromInt(1)));
        Assert.False(range.Contains(Quantity.FromInt(4)));
    }
}
