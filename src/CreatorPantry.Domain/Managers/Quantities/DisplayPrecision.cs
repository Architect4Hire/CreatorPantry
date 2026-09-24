using System.Globalization;
using System.Text.Json.Serialization;
using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Managers.Quantities;

/// <summary>
/// A validated count of fractional digits to display, bounded by the same policy already used for
/// <see cref="CreatorPantry.Domain.Modules.Measurement.Data.Entities.MeasurementUnit.DisplayPrecision"/>
/// (<see cref="QuantityFormat.MinDisplayPrecision"/>–<see cref="QuantityFormat.MaxDisplayPrecision"/>), so the
/// bound is declared once rather than duplicated at every call site that wants to validate one.
/// </summary>
/// <remarks>
/// Display precision is a presentation rule, not a statement of measurement accuracy — rounding a number to
/// three places does not make it right to three places (<see cref="QuantityFormat"/>).
/// </remarks>
[JsonConverter(typeof(DisplayPrecisionJsonConverter))]
public readonly struct DisplayPrecision : IEquatable<DisplayPrecision>, IComparable<DisplayPrecision>
{
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="value"/> is outside <see cref="QuantityFormat.MinDisplayPrecision"/>–<see cref="QuantityFormat.MaxDisplayPrecision"/>.
    /// </exception>
    public DisplayPrecision(int value)
    {
        if (value < QuantityFormat.MinDisplayPrecision || value > QuantityFormat.MaxDisplayPrecision)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"Display precision must be between {QuantityFormat.MinDisplayPrecision} and {QuantityFormat.MaxDisplayPrecision}.");
        }

        Value = value;
    }

    public int Value { get; }

    public bool Equals(DisplayPrecision other) => Value == other.Value;

    public override bool Equals(object? obj) => obj is DisplayPrecision other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public int CompareTo(DisplayPrecision other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
