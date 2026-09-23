namespace CreatorPantry.Domain.Managers.Reference;

/// <summary>
/// Limits and rules for ingredient density references and the sources that back them. Global reference data:
/// no <c>WorkspaceId</c> (tenancy.md).
/// </summary>
public static class DensityPolicy
{
    public const int ConditionNoteMaxLength = 128;

    public const int SourceCodeMaxLength = 64;

    public const int SourceNameMaxLength = 128;

    public const int SourceUrlMaxLength = 512;

    public const int SourceCitationMaxLength = 512;

    /// <inheritdoc cref="MeasurementPolicy.MinDisplayPrecision"/>
    public const int MinDisplayPrecision = MeasurementPolicy.MinDisplayPrecision;

    /// <inheritdoc cref="MeasurementPolicy.MaxDisplayPrecision"/>
    public const int MaxDisplayPrecision = MeasurementPolicy.MaxDisplayPrecision;

    /// <summary>
    /// SQL precision and scale for the recorded mass and volume. Wide enough to store a source's figures at
    /// their own scale, because the ratio is divided at the point of use rather than stored pre-rounded.
    /// </summary>
    public const string QuantityColumnType = "decimal(28, 12)";

    /// <summary>
    /// The <see cref="Data.IngredientDensityReference.NormalizedCondition"/> for a density that names no
    /// condition.
    /// </summary>
    /// <remarks>
    /// Empty string rather than null, deliberately. The normalized condition is part of the natural key, and
    /// providers disagree about NULL in a unique index: SQL Server treats two NULLs as equal and allows only one
    /// such row, while SQLite treats them as distinct and allows many. A nullable key column would therefore
    /// enforce a different rule in production than in the tests. Empty string behaves identically on both.
    /// </remarks>
    public const string UnspecifiedCondition = "";

    /// <summary>
    /// The lookup form of a condition note, so that "Spooned and Leveled" and "spooned and leveled" are one
    /// condition rather than two competing densities.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="IngredientPolicy.NormalizeName"/>: condition notes are open-ended multi-word phrases
    /// with the same needs as ingredient names — fold case, diacritics, and punctuation, keep word boundaries.
    /// Not <see cref="MeasurementPolicy.NormalizeAlias"/>, which removes separators outright and is only safe
    /// for short closed-set unit codes.
    /// </remarks>
    public static string NormalizeCondition(string conditionNote) => IngredientPolicy.NormalizeName(conditionNote);
}
