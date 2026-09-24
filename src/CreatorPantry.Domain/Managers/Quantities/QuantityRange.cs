using System.Text.Json.Serialization;

namespace CreatorPantry.Domain.Managers.Quantities;

/// <summary>
/// A low/high pair of exact <see cref="Quantity"/> values, such as "2–3 cups". Mirrors the intent of the
/// <c>CK_RecipeIngredients_Quantity_Range</c> database check: the upper bound is always strictly greater than
/// the lower one, so a range can never collapse to a single value or invert.
/// </summary>
[JsonConverter(typeof(QuantityRangeJsonConverter))]
public readonly struct QuantityRange : IEquatable<QuantityRange>
{
    private QuantityRange(Quantity lower, Quantity upper)
    {
        Lower = lower;
        Upper = upper;
    }

    public Quantity Lower { get; }

    public Quantity Upper { get; }

    /// <exception cref="ArgumentException"><paramref name="upper"/> is not strictly greater than <paramref name="lower"/>.</exception>
    public static QuantityRange Create(Quantity lower, Quantity upper)
    {
        if (upper.CompareTo(lower) <= 0)
            throw new ArgumentException("A range's upper bound must be strictly greater than its lower bound.", nameof(upper));

        return new QuantityRange(lower, upper);
    }

    /// <summary>
    /// Scales both bounds by <paramref name="factor"/>. A negative factor swaps which bound ends up smaller,
    /// so the result is re-ordered to keep <see cref="Lower"/> ≤ <see cref="Upper"/>.
    /// </summary>
    public QuantityRange Multiply(Quantity factor) => factor.IsNegative
        ? new QuantityRange(Upper.Multiply(factor), Lower.Multiply(factor))
        : new QuantityRange(Lower.Multiply(factor), Upper.Multiply(factor));

    public bool Contains(Quantity value) => value.CompareTo(Lower) >= 0 && value.CompareTo(Upper) <= 0;

    public bool Equals(QuantityRange other) => Lower.Equals(other.Lower) && Upper.Equals(other.Upper);

    public override bool Equals(object? obj) => obj is QuantityRange other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Lower, Upper);

    public override string ToString() => $"{Lower}–{Upper}";
}
