namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// A slice of an <see cref="IngredientLineTokens.OriginalText"/> that <see cref="IngredientLineTokenizer"/>
/// identified as one candidate — a quantity, a unit word, the ingredient name, a preparation clause, and so
/// on. Carries its own offset rather than just the substring, so a reviewing UI can highlight exactly what
/// the tokenizer used without re-searching the line.
/// </summary>
/// <param name="Start">The zero-based character offset into <see cref="IngredientLineTokens.OriginalText"/>.</param>
/// <param name="Length">The span's length in characters.</param>
/// <param name="Text">The substring itself, equal to <c>OriginalText.Substring(Start, Length)</c>.</param>
public readonly record struct IngredientLineSpan(int Start, int Length, string Text);
