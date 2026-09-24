using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Managers.Quantities.Persistence;

namespace CreatorPantry.Tests.Quantities;

/// <summary>
/// The EF Core <c>ValueConverter</c>/<c>ValueComparer</c> pairs are not wired to any entity yet (7.1 adds the
/// primitives only), so these tests exercise the converters directly rather than through a <c>DbContext</c>.
/// </summary>
public sealed class PersistenceConverterTests
{
    [Fact]
    public void Quantity_round_trips_through_its_JSON_column_converter()
    {
        var converter = new QuantityValueConverter();
        var quantity = Quantity.FromFraction(1, 3);

        var stored = Assert.IsType<string>(converter.ConvertToProvider(quantity));
        var restored = Assert.IsType<Quantity>(converter.ConvertFromProvider(stored));

        Assert.Equal(quantity, restored);
    }

    [Fact]
    public void Quantity_comparer_treats_equal_reduced_fractions_as_equal()
    {
        var comparer = new QuantityValueComparer();

        Assert.True(comparer.Equals(Quantity.FromFraction(2, 4), Quantity.FromFraction(1, 2)));
        Assert.Equal(
            comparer.GetHashCode(Quantity.FromFraction(2, 4)),
            comparer.GetHashCode(Quantity.FromFraction(1, 2)));
    }

    [Fact]
    public void QuantityRange_round_trips_through_its_JSON_column_converter()
    {
        var converter = new QuantityRangeValueConverter();
        var range = QuantityRange.Create(Quantity.FromInt(2), Quantity.FromInt(3));

        var stored = Assert.IsType<string>(converter.ConvertToProvider(range));
        var restored = Assert.IsType<QuantityRange>(converter.ConvertFromProvider(stored));

        Assert.Equal(range, restored);
    }

    [Fact]
    public void QualitativeQuantity_round_trips_through_a_plain_text_column_converter()
    {
        var converter = new QualitativeQuantityValueConverter();
        var quantity = new QualitativeQuantity("to taste");

        var stored = Assert.IsType<string>(converter.ConvertToProvider(quantity));
        Assert.Equal("to taste", stored);

        var restored = Assert.IsType<QualitativeQuantity>(converter.ConvertFromProvider(stored));
        Assert.Equal(quantity, restored);
    }

    [Fact]
    public void DisplayPrecision_round_trips_through_a_plain_int_column_converter()
    {
        var converter = new DisplayPrecisionValueConverter();
        var precision = new DisplayPrecision(4);

        var stored = Assert.IsType<int>(converter.ConvertToProvider(precision));
        Assert.Equal(4, stored);

        var restored = Assert.IsType<DisplayPrecision>(converter.ConvertFromProvider(stored));
        Assert.Equal(precision, restored);
    }
}
