using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// A catalogue unit an ingredient line's unit text was recognised as, with the dimension the line must store
/// beside it.
/// </summary>
public sealed record ResolvedIngredientUnit(Guid MeasurementUnitId, MeasurementDimension Dimension);

/// <summary>
/// Which free-text units on generated ingredient lines may be recorded as CreatorPantry catalogue units.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> A model may not name an identifier (ai.md), so a generated line carries
/// its unit only as words — "cups", "Tbsp", "cloves". Left like that, the line has no
/// <c>MeasurementUnitId</c>, and scaling, conversion, nutrition and export all treat it as unmeasured. This is
/// the deterministic step that turns those words into the catalogue's own unit, and it is the same exact/alias
/// match a creator's pasted line gets (<see cref="UnitMatcher"/>), never a fuzzy or model-chosen one.
/// </para>
/// <para>
/// <strong>Additive only.</strong> The unit text is never changed: recipes.md keeps the creator's word beside the
/// recognised id, and a line the catalogue does not know keeps its word and simply has no id. That line is what
/// the editor and the readiness checklist flag for the creator, because it will not scale or convert.
/// </para>
/// <para>
/// <strong>Three answers are deliberately not a match.</strong> Ambiguous text — the same word naming two units at
/// the same precedence — stays unresolved rather than guessed, as <see cref="UnitMatchResult.IsAmbiguous"/>
/// promises. A temperature unit is refused, because <c>CK_RecipeIngredients_Unit_Dimension</c> refuses an
/// ingredient measured in degrees and the create validator would reject the whole draft over it. And a unit
/// the catalogue no longer offers for new input is left alone for the same reason.
/// </para>
/// </remarks>
public static class IngredientUnitResolution
{
    /// <summary>
    /// The form a unit text is looked up by: trimmed, and null when nothing is left.
    /// </summary>
    /// <remarks>
    /// The same trim the proposal application applies to a <c>unitText</c> change, so the text the facade
    /// resolved and the text the line ends up holding are one string. The catalogue match itself normalizes
    /// further (case, punctuation); that is <see cref="MeasurementPolicy"/>'s business, not this key's.
    /// </remarks>
    public static string? Key(string? unitText)
    {
        var trimmed = unitText?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// The distinct unit texts worth asking the catalogue about, in first-seen order.
    /// </summary>
    /// <remarks>
    /// Text longer than the match seam accepts is left out rather than allowed to fail the whole batch: no unit in
    /// the catalogue is that long, so it could only ever come back unresolved, and one runaway span must not cost
    /// every other line its unit.
    /// </remarks>
    public static IReadOnlyList<string> Candidates(IEnumerable<string?> unitTexts) =>
        [.. unitTexts
            .Select(Key)
            .OfType<string>()
            .Where(text => text.Length <= ReferencePolicy.MaxMatchCandidateLength)
            .Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Pairs each candidate with the unit it confidently resolved to, keeping only those an ingredient line may
    /// record.
    /// </summary>
    /// <param name="candidates">The texts asked about, in the order they were asked.</param>
    /// <param name="matches">The catalogue's answers, one per candidate and in the same order.</param>
    /// <param name="usableUnits">
    /// The units still offered for new input, by id — the source of each unit's dimension. A resolved id missing
    /// from here is a retired unit and is not recorded.
    /// </param>
    public static IReadOnlyDictionary<string, ResolvedIngredientUnit> Select(
        IReadOnlyList<string> candidates,
        IReadOnlyList<UnitMatchResult> matches,
        IReadOnlyDictionary<Guid, MeasurementUnitServiceModel> usableUnits)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(usableUnits);

        if (candidates.Count != matches.Count)
        {
            throw new ArgumentException("Every candidate needs exactly one match result.", nameof(matches));
        }

        var resolved = new Dictionary<string, ResolvedIngredientUnit>(StringComparer.Ordinal);

        for (var index = 0; index < candidates.Count; index++)
        {
            var match = matches[index];

            if (match.IsAmbiguous || match.Resolved is not { } candidate) continue;
            if (!usableUnits.TryGetValue(candidate.MeasurementUnitId, out var unit)) continue;
            if (unit.Dimension == MeasurementDimension.Temperature) continue;

            resolved[candidates[index]] = new ResolvedIngredientUnit(unit.Id, unit.Dimension);
        }

        return resolved;
    }

    /// <summary>
    /// The unit a line's text was recognised as, or null when it was not — including when it has no unit text.
    /// </summary>
    public static ResolvedIngredientUnit? For(
        string? unitText, IReadOnlyDictionary<string, ResolvedIngredientUnit> resolved) =>
        Key(unitText) is { } key && resolved.TryGetValue(key, out var unit) ? unit : null;
}
