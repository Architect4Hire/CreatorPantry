using System.Text.Json.Serialization;

namespace CreatorPantry.Domain.Managers.Quantities;

/// <summary>
/// A non-numeric amount — "to taste", "a pinch", "as needed" — preserved verbatim rather than resolved to a
/// number.
/// </summary>
/// <remarks>
/// <para>
/// This type deliberately does not classify the descriptor into a fixed vocabulary of qualitative phrases.
/// Deciding that a line reads as qualitative at all, and what its descriptor should be, is tokenizing and
/// matching free text — the ingredient-line tokenizer's job (7.2), not a value object's. All this type does is
/// hold the creator's own words and mark, by its presence, that the amount is not a number: it has nothing to
/// scale, convert, or round, and a recipe line carrying one is flagged for review rather than treated as
/// missing data (recipes.md — "preserve non-scalable language... as review flags").
/// </para>
/// <para>
/// <c>default(QualitativeQuantity)</c> has a <see langword="null"/> <see cref="Descriptor"/>: a struct's
/// default cannot run its constructor. There is no meaningful empty state for this type, so a default instance
/// is not a valid one — construct it through <see cref="QualitativeQuantity(string)"/> or not at all.
/// </para>
/// </remarks>
[JsonConverter(typeof(QualitativeQuantityJsonConverter))]
public readonly struct QualitativeQuantity : IEquatable<QualitativeQuantity>
{
    /// <exception cref="ArgumentException"><paramref name="descriptor"/> is null, empty, or whitespace.</exception>
    public QualitativeQuantity(string descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor))
            throw new ArgumentException("A qualitative quantity requires a non-empty descriptor.", nameof(descriptor));

        Descriptor = descriptor.Trim();
    }

    /// <summary>The creator's own words, trimmed but otherwise verbatim.</summary>
    public string Descriptor { get; }

    public bool Equals(QualitativeQuantity other) => Descriptor == other.Descriptor;

    public override bool Equals(object? obj) => obj is QualitativeQuantity other && Equals(other);

    public override int GetHashCode() => Descriptor?.GetHashCode(StringComparison.Ordinal) ?? 0;

    public override string ToString() => Descriptor ?? string.Empty;
}
