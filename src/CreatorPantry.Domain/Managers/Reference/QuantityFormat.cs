namespace CreatorPantry.Domain.Managers.Reference;

/// <summary>
/// How a measured quantity is stored and rendered, shared by the unit catalogue and the density references
/// that convert through it.
/// </summary>
/// <remarks>
/// <para>
/// <c>decimal(28, 12)</c> is chosen, not inherited: the twelve fractional digits hold the smallest factors in
/// use exactly enough for recipe arithmetic — a US teaspoon is 4.92892159375 ml — and the integral range
/// covers the largest, a US gallon at 3785.411784 ml. The EF default of <c>decimal(18, 2)</c> would silently
/// round a teaspoon to 4.93 and put that error into every scaled recipe.
/// </para>
/// <para>
/// Display precision is a presentation rule and explicitly <em>not</em> a statement of measurement accuracy,
/// uncertainty or confidence. Rounding a number to three places does not make it right to three places.
/// </para>
/// </remarks>
public static class QuantityFormat
{
    public const int MinDisplayPrecision = 0;

    public const int MaxDisplayPrecision = 6;

    public const string DecimalColumnType = "decimal(28, 12)";
}
