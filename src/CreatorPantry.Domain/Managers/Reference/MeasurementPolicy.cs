namespace CreatorPantry.Domain.Managers.Reference;

/// <summary>
/// Limits and invariants for the global measurement vocabulary, shared by EF configuration, seed data, and
/// any later validation. These entities are platform reference data: they have no <c>WorkspaceId</c> and are
/// never duplicated per workspace (tenancy.md).
/// </summary>
public static class MeasurementPolicy
{
    /// <summary>Stable machine key, e.g. <c>g</c>, <c>ml</c>, <c>tsp</c>, <c>floz-us</c>, <c>each</c>.</summary>
    public const int CodeMaxLength = 32;

    /// <summary>Lowercase alphanumeric segments joined by single hyphens; no leading, trailing, or doubled hyphen.</summary>
    public const string CodePattern = "^[a-z0-9]+(-[a-z0-9]+)*$";

    public const int DisplayNameMaxLength = 64;

    public const int AbbreviationMaxLength = 16;

    public const int AliasMaxLength = 64;

    /// <summary>Decimal places allowed for <see cref="Data.MeasurementUnit.DisplayPrecision"/>.</summary>
    public const int MinDisplayPrecision = 0;

    /// <inheritdoc cref="MinDisplayPrecision"/>
    public const int MaxDisplayPrecision = 6;

    /// <summary>
    /// SQL precision and scale for <see cref="Data.MeasurementUnit.BaseUnitFactor"/>. Twelve decimal places
    /// hold the smallest factors in use exactly enough for recipe arithmetic, and the integral range covers
    /// the largest (a US gallon is 3785.411784 ml).
    /// </summary>
    public const string BaseUnitFactorColumnType = "decimal(28, 12)";

    /// <summary>The <see cref="Data.MeasurementUnit.Code"/> every factor in a dimension is expressed against.</summary>
    /// <returns>The base unit's code, or <c>null</c> for dimensions that have no multiplicative base.</returns>
    public static string? BaseUnitCode(MeasurementDimension dimension) => dimension switch
    {
        MeasurementDimension.Mass => "g",
        MeasurementDimension.Volume => "ml",
        MeasurementDimension.Count => "each",
        _ => null,
    };

    /// <summary>
    /// Whether a dimension's units carry a <see cref="Data.MeasurementUnit.BaseUnitFactor"/>. False for
    /// <see cref="MeasurementDimension.Temperature"/> (affine, not multiplicative) and
    /// <see cref="MeasurementDimension.Qualitative"/> (no numeric relationship at all).
    /// </summary>
    public static bool RequiresBaseUnitFactor(MeasurementDimension dimension) =>
        BaseUnitCode(dimension) is not null;

    /// <summary>
    /// The dimension a filter names, or <c>null</c> when it names none.
    /// </summary>
    /// <remarks>
    /// Matched against the declared names rather than delegating to <see cref="Enum.TryParse{T}(string, bool, out T)"/>,
    /// which also accepts two forms this filter never meant to publish: a numeric string (<c>"99"</c> parses
    /// happily into an undefined member, and the query then returns an empty page with a 200 where the
    /// documented answer is a 400), and a comma-delimited list (<c>"Mass,Volume"</c> bitwise-ORs into a
    /// single unintended value). Both would be accepted today and refused later, and api-contract.md counts
    /// narrowing an accepted value as a breaking change — so the narrow reading is the one that ships first.
    /// </remarks>
    public static MeasurementDimension? ParseDimension(string? dimension)
    {
        var value = dimension?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        foreach (var name in Enum.GetNames<MeasurementDimension>())
        {
            if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<MeasurementDimension>(name);
            }
        }

        return null;
    }

    /// <summary>Whether a supplied dimension filter is one this API accepts. An absent filter is valid.</summary>
    public static bool IsAcceptedDimension(string? dimension) =>
        string.IsNullOrWhiteSpace(dimension) || ParseDimension(dimension) is not null;

    /// <summary>
    /// Whether a quantity in one unit may be converted into another by arithmetic alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two refusals, for two different reasons.
    /// </para>
    /// <para>
    /// <strong>Across dimensions</strong>, always false. Mass and volume are bridged only by an ingredient's
    /// own cited density, which is a different operation with a different input and lives in
    /// <see cref="Data.IngredientDensityReference"/>. Temperature is affine and has no factor at all.
    /// </para>
    /// <para>
    /// <strong>Between two different count nouns</strong>, also false, and this is the one the type system
    /// cannot see. Every <see cref="MeasurementDimension.Count"/> unit carries a base factor of <c>1</c>,
    /// meaning "one of these is one item" — which makes <c>1 bunch = 1 clove</c> arithmetically available to
    /// anything holding two unit rows, including an API client or a model reading
    /// <c>/api/v1/reference/units</c>. The factor exists so a count quantity <em>scales</em>; it was never a
    /// conversion rate between nouns, and one head is not one clove. This method is where that rule is
    /// enforceable rather than merely written down.
    /// </para>
    /// <para>
    /// Same dimension and same code is trivially true, which is what lets a caller ask without special-casing
    /// the identity. If a numeric count unit such as <c>dozen</c> is ever seeded, it belongs to an explicit
    /// allowlist here — not to a relaxation of the rule.
    /// </para>
    /// </remarks>
    public static bool MayConvert(
        MeasurementDimension fromDimension,
        string fromCode,
        MeasurementDimension toDimension,
        string toCode)
    {
        if (fromDimension != toDimension)
        {
            return false;
        }

        if (fromDimension is MeasurementDimension.Count)
        {
            return string.Equals(fromCode, toCode, StringComparison.Ordinal);
        }

        // Qualitative units ("a pinch") have no numeric relationship to anything, including each other.
        return fromDimension is not MeasurementDimension.Qualitative
            || string.Equals(fromCode, toCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// The lookup form of an alias: trimmed, lowercased, and stripped of the punctuation and whitespace that
    /// creators and imports vary freely ("Tsp.", "tsp", "TSP" all normalize to <c>tsp</c>).
    /// </summary>
    /// <remarks>
    /// Because this lowercases, case alone cannot distinguish two aliases. That is why the case-ambiguous
    /// single letters <c>T</c> (tablespoon) and <c>t</c> (teaspoon) are not part of the global alias set: they
    /// are ambiguous to readers too, so resolving them needs the surrounding recipe line and belongs to
    /// parsing, not to a globally unique reference key.
    /// </remarks>
    public static string NormalizeAlias(string alias) =>
        new string(alias.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
