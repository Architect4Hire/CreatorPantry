using System.Globalization;
using System.Numerics;
using System.Text.Json.Serialization;

namespace CreatorPantry.Domain.Managers.Quantities;

/// <summary>
/// An exact quantity, held as a reduced fraction rather than a <see cref="decimal"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>decimal(28, 12)</c> is precise but not exact: a US teaspoon fits in twelve fractional digits, but
/// <c>1/3 cup</c> does not terminate in base ten at any precision. Scaling that decimal by three and back
/// does not return the original value — the error is small, but it is there, and it compounds every time a
/// recipe is scaled again. A fraction has no such error: <see cref="Multiply"/> is exact for any factor,
/// forever, because the numerator and denominator are arbitrary-precision integers rather than a fixed-width
/// approximation.
/// </para>
/// <para>
/// <see cref="Denominator"/> is always positive and the fraction is always reduced to lowest terms at
/// construction, so two quantities are equal exactly when their numerator and denominator are equal — no
/// tolerance, no epsilon comparison.
/// </para>
/// <para>
/// Converting back to <see cref="decimal"/> is necessarily lossy for a non-terminating fraction, so
/// <see cref="ToDecimal"/> takes an explicit scale and rounding mode rather than picking one silently
/// (recipes.md — "documented rounding").
/// </para>
/// </remarks>
[JsonConverter(typeof(QuantityJsonConverter))]
public readonly struct Quantity : IEquatable<Quantity>, IComparable<Quantity>
{
    private Quantity(BigInteger numerator, BigInteger denominator)
    {
        Numerator = numerator;
        Denominator = denominator;
    }

    /// <summary>The fraction's numerator. Carries the sign; may be zero or negative.</summary>
    public BigInteger Numerator { get; }

    /// <summary>The fraction's denominator. Always strictly positive.</summary>
    public BigInteger Denominator { get; }

    /// <summary>Zero, represented as <c>0/1</c>.</summary>
    public static readonly Quantity Zero = new(BigInteger.Zero, BigInteger.One);

    public bool IsZero => Numerator.IsZero;

    public bool IsNegative => Numerator.Sign < 0;

    /// <summary>
    /// Builds a quantity from a numerator and denominator, reducing to lowest terms and normalizing the sign
    /// onto the numerator.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="denominator"/> is zero.</exception>
    public static Quantity FromFraction(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero)
            throw new ArgumentException("A quantity's denominator cannot be zero.", nameof(denominator));

        if (denominator.Sign < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        var gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);

        return new Quantity(numerator / gcd, denominator / gcd);
    }

    /// <summary>
    /// Builds a quantity from a <see cref="decimal"/> exactly — no binary floating-point conversion is
    /// involved. <see cref="decimal"/> already stores its value as an integer mantissa and a base-ten scale
    /// (<see cref="decimal.GetBits(decimal, Span{int})"/>), so the fraction is read off directly:
    /// <c>mantissa / 10^scale</c>.
    /// </summary>
    public static Quantity FromDecimal(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);

        var isNegative = (bits[3] & unchecked((int)0x80000000)) != 0;
        var scale = (bits[3] >> 16) & 0x7F;

        var mantissa = new BigInteger((uint)bits[0])
            | (new BigInteger((uint)bits[1]) << 32)
            | (new BigInteger((uint)bits[2]) << 64);

        return FromFraction(isNegative ? -mantissa : mantissa, BigInteger.Pow(10, scale));
    }

    public static Quantity FromInt(long value) => FromFraction(value, BigInteger.One);

    public Quantity Add(Quantity other) => FromFraction(
        (Numerator * other.Denominator) + (other.Numerator * Denominator),
        Denominator * other.Denominator);

    public Quantity Subtract(Quantity other) => FromFraction(
        (Numerator * other.Denominator) - (other.Numerator * Denominator),
        Denominator * other.Denominator);

    /// <summary>The operation recipe scaling (7.6) calls — exact for any factor.</summary>
    public Quantity Multiply(Quantity factor) => FromFraction(
        Numerator * factor.Numerator,
        Denominator * factor.Denominator);

    /// <summary>
    /// Rounds this quantity to a decimal with the given number of fractional digits. Round-half behavior is
    /// explicit because the correct answer to "how should this round" is a presentation policy, not a
    /// property of the number (recipes.md).
    /// </summary>
    /// <param name="scale">The number of fractional digits to keep, from 0 to 28 (decimal's maximum).</param>
    /// <param name="rounding">
    /// <see cref="MidpointRounding.ToEven"/> (banker's rounding, the default) or
    /// <see cref="MidpointRounding.AwayFromZero"/>. No other mode is meaningful for an exact rational.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> is outside 0–28, or
    /// <paramref name="rounding"/> is neither supported mode.</exception>
    /// <exception cref="OverflowException">The rounded value does not fit in a <see cref="decimal"/>.</exception>
    public decimal ToDecimal(int scale, MidpointRounding rounding = MidpointRounding.ToEven)
    {
        if (scale is < 0 or > 28)
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Scale must be between 0 and 28 — decimal's maximum.");

        if (rounding is not (MidpointRounding.ToEven or MidpointRounding.AwayFromZero))
            throw new ArgumentOutOfRangeException(nameof(rounding), rounding, "Only ToEven and AwayFromZero are meaningful for an exact rational.");

        var scaledNumerator = Numerator * BigInteger.Pow(10, scale);
        var (quotient, remainder) = BigInteger.DivRem(scaledNumerator, Denominator);

        if (!remainder.IsZero)
        {
            var twiceRemainder = BigInteger.Abs(remainder) * 2;

            var roundsUp = rounding == MidpointRounding.AwayFromZero
                ? twiceRemainder >= Denominator
                : twiceRemainder > Denominator || (twiceRemainder == Denominator && !quotient.IsEven);

            if (roundsUp)
                quotient += Numerator.Sign < 0 ? -1 : 1;
        }

        return (decimal)quotient / (decimal)BigInteger.Pow(10, scale);
    }

    public bool Equals(Quantity other) => Numerator == other.Numerator && Denominator == other.Denominator;

    public override bool Equals(object? obj) => obj is Quantity other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);

    /// <summary>Cross-multiplies rather than converting to decimal, so ordering is as exact as equality.</summary>
    public int CompareTo(Quantity other) => (Numerator * other.Denominator).CompareTo(other.Numerator * Denominator);

    public static bool operator ==(Quantity left, Quantity right) => left.Equals(right);

    public static bool operator !=(Quantity left, Quantity right) => !left.Equals(right);

    public static bool operator <(Quantity left, Quantity right) => left.CompareTo(right) < 0;

    public static bool operator <=(Quantity left, Quantity right) => left.CompareTo(right) <= 0;

    public static bool operator >(Quantity left, Quantity right) => left.CompareTo(right) > 0;

    public static bool operator >=(Quantity left, Quantity right) => left.CompareTo(right) >= 0;

    /// <summary>Debug-only fraction notation (<c>"1/3"</c>) — not creator-facing display formatting (ING-006).</summary>
    public override string ToString() => $"{Numerator.ToString(CultureInfo.InvariantCulture)}/{Denominator.ToString(CultureInfo.InvariantCulture)}";
}
