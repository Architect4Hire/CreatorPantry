namespace CreatorPantry.Domain.Managers.Reference;

/// <summary>
/// The kind of quantity a <see cref="Data.MeasurementUnit"/> measures. A unit only ever converts within its
/// own dimension: crossing from <see cref="Volume"/> to <see cref="Mass"/> requires ingredient-specific
/// density data that this reference model deliberately does not contain (recipes.md, B-08).
/// </summary>
/// <remarks>
/// These numeric values are persisted <em>and</em> named literally by the
/// <c>CK_MeasurementUnits_Factor_Dimension</c> check constraint. Do not renumber or reorder them; append new
/// dimensions at the end and update that constraint in the same migration.
/// </remarks>
public enum MeasurementDimension
{
    /// <summary>Weight. Base unit: gram.</summary>
    Mass = 0,

    /// <summary>Capacity. Base unit: millilitre.</summary>
    Volume = 1,

    /// <summary>Discrete items ("2 eggs", "1 dozen"). Base unit: each.</summary>
    Count = 2,

    /// <summary>
    /// Oven, liquid, and internal temperatures. Carries no base-unit factor: the relationship between scales
    /// is affine (<c>°F = °C × 9/5 + 32</c>), which a single multiplier cannot express.
    /// </summary>
    Temperature = 3,

    /// <summary>
    /// Units with no numeric relationship to anything — "pinch", "to taste", "handful". Never scaled or
    /// converted; a recipe line using one is flagged for creator review instead (recipes.md).
    /// </summary>
    Qualitative = 4,
}
