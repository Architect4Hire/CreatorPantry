namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// A reason <see cref="IngredientLineTokenizer"/> left something unresolved rather than guessing at it.
/// </summary>
/// <remarks>
/// This is deliberately a short, closed list. It does not grow to cover every way a match could later fail —
/// alias, plural, punctuation, and no-match ambiguity belong to the reference matcher (ING-001, 7.3), which
/// has a vocabulary to check against. The tokenizer has no vocabulary, so it can only ever say "this position
/// held nothing recognizable" or "this shape did not hold a valid value" — never "this word is wrong".
/// </remarks>
public enum IngredientLineAmbiguityKind
{
    /// <summary>No numeric quantity or range was found anywhere in the line.</summary>
    /// <remarks>
    /// Covers both a genuinely qualitative line ("salt to taste") and a line that opens with something
    /// number-shaped but not valid quantity grammar ("9x13-inch pan"). The tokenizer does not try to tell
    /// these apart — that would mean guessing at meaning from a closed vocabulary of phrases, which is
    /// exactly the catalogue matching this prompt is scoped out of.
    /// </remarks>
    NoQuantityDetected = 0,

    /// <summary>
    /// The text matched a quantity or range's shape, but the value did not construct — a zero denominator, or
    /// a range whose upper bound does not exceed its lower one.
    /// </summary>
    InvalidQuantity = 1,
}

/// <summary>One unresolved position in a tokenized line.</summary>
public sealed record IngredientLineAmbiguity(IngredientLineAmbiguityKind Kind, IngredientLineSpan Span, string Message);
