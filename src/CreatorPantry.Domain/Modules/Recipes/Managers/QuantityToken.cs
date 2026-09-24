using CreatorPantry.Domain.Managers.Quantities;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// A quantity or range candidate found by <see cref="IngredientLineTokenizer"/>, before any catalogue
/// matching. Exactly one of <see cref="Value"/> and <see cref="Range"/> is set, unless <see cref="IsInvalid"/>
/// is <see langword="true"/>, in which case neither is: the text matched a numeric <em>shape</em> (a
/// fraction, a range) but the value itself did not construct — a zero denominator, or a range whose upper
/// bound does not exceed its lower one. The raw text is preserved in <see cref="Span"/> either way; nothing
/// is invented in its place.
/// </summary>
public sealed record QuantityToken
{
    public required IngredientLineSpan Span { get; init; }

    public Quantity? Value { get; init; }

    public QuantityRange? Range { get; init; }

    public bool IsInvalid { get; init; }
}
