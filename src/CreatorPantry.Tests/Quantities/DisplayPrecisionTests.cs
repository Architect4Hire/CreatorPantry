using System.Text.Json;
using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Managers.Reference;
using CsCheck;

namespace CreatorPantry.Tests.Quantities;

public sealed class DisplayPrecisionTests
{
    private static readonly Gen<int> ValidValue =
        Gen.Int[QuantityFormat.MinDisplayPrecision, QuantityFormat.MaxDisplayPrecision];

    private static readonly Gen<int> InvalidValue = Gen.OneOf(
        Gen.Int[int.MinValue, QuantityFormat.MinDisplayPrecision - 1],
        Gen.Int[QuantityFormat.MaxDisplayPrecision + 1, int.MaxValue]);

    [Fact]
    public void Round_trips_through_the_JSON_contract_as_a_plain_number()
    {
        ValidValue.Sample(value =>
        {
            var precision = new DisplayPrecision(value);
            var json = JsonSerializer.Serialize(precision);

            Assert.Equal(value.ToString(System.Globalization.CultureInfo.InvariantCulture), json);
            Assert.Equal(precision, JsonSerializer.Deserialize<DisplayPrecision>(json));
        });
    }

    [Fact]
    public void A_value_within_the_bounds_is_accepted()
    {
        ValidValue.Sample(value => Assert.Equal(value, new DisplayPrecision(value).Value));
    }

    [Fact]
    public void A_value_outside_the_bounds_is_rejected()
    {
        InvalidValue.Sample(value => Assert.Throws<ArgumentOutOfRangeException>(() => new DisplayPrecision(value)));
    }

    [Fact]
    public void Ordering_matches_the_underlying_integer()
    {
        Gen.Select(ValidValue, ValidValue).Sample((a, b) =>
            Assert.Equal(a.CompareTo(b), new DisplayPrecision(a).CompareTo(new DisplayPrecision(b))));
    }
}
