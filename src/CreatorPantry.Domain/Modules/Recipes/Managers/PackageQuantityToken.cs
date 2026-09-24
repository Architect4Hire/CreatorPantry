namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// A package-size aside immediately after the line's leading quantity, such as the <c>(14.5 oz)</c> in
/// <c>"1 (14.5 oz) can diced tomatoes"</c> — the item count is the line's own <see cref="QuantityToken"/>;
/// this is the package's own nested quantity and unit, preserved alongside it rather than merged or discarded.
/// </summary>
public sealed record PackageQuantityToken
{
    /// <summary>The whole parenthetical, including the parentheses.</summary>
    public required IngredientLineSpan Span { get; init; }

    public required QuantityToken Quantity { get; init; }

    public required IngredientLineSpan Unit { get; init; }
}
