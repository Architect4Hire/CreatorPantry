namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>The result of tokenizing one free-form recipe ingredient line. See <see cref="IngredientLineTokenizer"/>.</summary>
public sealed record IngredientLineTokens
{
    /// <summary>The line exactly as given — never trimmed, rewritten, or normalized.</summary>
    public required string OriginalText { get; init; }

    /// <summary>
    /// True when the whole line is a section header ("For the crust:"), not an ingredient. Every other
    /// property is left at its default when this is true.
    /// </summary>
    public bool IsGroupMarker { get; init; }

    public QuantityToken? Quantity { get; init; }

    /// <summary>The package-size aside in a line such as <c>"1 (14.5 oz) can diced tomatoes"</c>, if any.</summary>
    public PackageQuantityToken? PackageQuantity { get; init; }

    /// <summary>
    /// The single word immediately after the quantity, if there is one — an unverified candidate, not a
    /// confirmed unit. The reference matcher (7.3) decides whether it names a real unit.
    /// </summary>
    public IngredientLineSpan? UnitCandidate { get; init; }

    public IngredientLineSpan? IngredientText { get; init; }

    /// <summary>Trailing comma-separated clauses after the ingredient text, in order, e.g. <c>["sifted", "cooled"]</c>.</summary>
    public IReadOnlyList<IngredientLineSpan> PreparationText { get; init; } = [];

    /// <summary>True when the line ends with a <c>(optional)</c> or <c>, optional</c> marker.</summary>
    public bool IsOptional { get; init; }

    /// <summary>Positions the tokenizer could not resolve deterministically. See <see cref="IngredientLineAmbiguityKind"/>.</summary>
    public IReadOnlyList<IngredientLineAmbiguity> Ambiguities { get; init; } = [];
}
