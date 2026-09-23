using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Modules.Measurement.Data.Entities;

/// <summary>
/// A surface form that resolves to one <see cref="MeasurementUnit"/> — "tsp.", "teaspoons", "gramme". Global
/// reference data with no <c>WorkspaceId</c>, like the unit it points at.
/// </summary>
/// <remarks>
/// Aliases exist so imported and hand-typed recipe lines can be <em>recognised</em>. Matching one records a
/// reference alongside the creator's own wording; it never rewrites the line (recipes.md).
/// </remarks>
public class UnitAlias
{
    public Guid Id { get; set; }

    public Guid MeasurementUnitId { get; set; }

    /// <summary>The alias as written, punctuation and casing intact, for display and provenance.</summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>
    /// The lookup key produced by <see cref="MeasurementPolicy.NormalizeAlias"/>. Unique across every unit,
    /// not merely within one: an alias that matched two units would make a recipe line ambiguous, so the
    /// database refuses to store that ambiguity in the first place.
    /// </summary>
    public string NormalizedAlias { get; set; } = string.Empty;
}
