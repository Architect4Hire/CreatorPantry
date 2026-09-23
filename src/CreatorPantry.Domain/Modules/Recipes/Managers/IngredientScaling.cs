namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// How one recipe ingredient line behaves when the recipe is scaled.
/// </summary>
/// <remarks>
/// <para>
/// recipes.md requires that non-scalable language — "to taste", package sizes, pan constraints, discrete
/// items — survives scaling as a review flag rather than being multiplied into nonsense. This is where that
/// survives as structured data instead of as a guess made fresh by whatever code happens to be scaling.
/// </para>
/// <para>
/// This records an intent, never a result. The arithmetic itself, the rounding, and the decision to refuse a
/// conversion belong to deterministic domain code (recipes.md, ai.md); nothing here performs or authorises a
/// calculation.
/// </para>
/// </remarks>
public enum IngredientScaling
{
    /// <summary>The quantity multiplies with the recipe. The default, and the ordinary case.</summary>
    Proportional = 0,

    /// <summary>
    /// The quantity stays as written — a tablespoon of salt for the pasta water, a 9-inch pan's worth of
    /// butter for greasing. Scaling leaves it untouched and says so.
    /// </summary>
    Fixed = 1,

    /// <summary>
    /// The line cannot be scaled without a human deciding what it should become: "to taste", "1 package",
    /// "2 eggs" at a half batch. Scaling surfaces it for the creator instead of choosing.
    /// </summary>
    ReviewRequired = 2,
}
