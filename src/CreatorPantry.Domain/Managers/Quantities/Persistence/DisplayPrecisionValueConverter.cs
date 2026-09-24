using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CreatorPantry.Domain.Managers.Quantities.Persistence;

/// <summary>
/// Maps <see cref="DisplayPrecision"/> to a plain <c>int</c> column — the same shape
/// <c>MeasurementUnit.DisplayPrecision</c> already uses, so adopting this type changes validation, not the
/// column. Not wired to any entity by this prompt.
/// </summary>
public sealed class DisplayPrecisionValueConverter : ValueConverter<DisplayPrecision, int>
{
    public DisplayPrecisionValueConverter()
        : base(
            precision => precision.Value,
            value => new DisplayPrecision(value))
    {
    }
}

/// <summary>Structural equality for <see cref="DisplayPrecision"/> inside EF's change tracker.</summary>
public sealed class DisplayPrecisionValueComparer : ValueComparer<DisplayPrecision>
{
    public DisplayPrecisionValueComparer()
        : base(
            (left, right) => left.Equals(right),
            precision => precision.GetHashCode())
    {
    }
}
