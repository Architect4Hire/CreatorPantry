namespace CreatorPantry.Domain.Managers.Reference;

/// <summary>
/// Limits for the shared food-category vocabulary — the coarse grouping an ingredient belongs to ("dairy",
/// "baking", "produce"). Global reference data with no <c>WorkspaceId</c>.
/// </summary>
/// <remarks>
/// A creator's own tags are workspace-owned and belong nowhere near this table (tenancy.md): this catalogue
/// holds shared facts only.
/// </remarks>
public static class FoodCategoryPolicy
{
    /// <summary>Stable lowercase machine key, e.g. <c>dairy</c>, <c>baking</c>, <c>fresh-produce</c>.</summary>
    public const int CodeMaxLength = 32;

    /// <inheritdoc cref="MeasurementPolicy.CodePattern"/>
    public const string CodePattern = MeasurementPolicy.CodePattern;

    public const int DisplayNameMaxLength = 64;
}
