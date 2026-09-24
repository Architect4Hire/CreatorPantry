namespace CreatorPantry.Domain.Modules.Measurement.Managers;

/// <summary>
/// Resolves free-form candidate text against the flattened unit/alias index — exact string equality on
/// normalized text only, never a fuzzy or partial match.
/// </summary>
/// <remarks>
/// Pure and stateless: given the same index and the same text twice, it answers the same way twice. See
/// <see cref="UnitMatchKind"/>'s remarks for why real ambiguity is achievable on this catalogue in a way it
/// is not for ingredients.
/// </remarks>
public static class UnitMatcher
{
    public static UnitMatchResult Resolve(string inputText, ILookup<string, UnitMatchIndexEntry> index)
    {
        var normalized = MeasurementPolicy.NormalizeAlias(inputText);
        var hits = index[normalized].ToList();

        if (hits.Count == 0)
        {
            return new UnitMatchResult { InputText = inputText };
        }

        // Collapse to one entry per unit first: a unit's own Code, DisplayName, Abbreviation, or alias can
        // all normalize to the same text, and that is not a tie between two things — it is one unit found
        // several ways.
        var perUnit = hits
            .GroupBy(hit => hit.MeasurementUnitId)
            .Select(group => group.OrderBy(hit => hit.Kind).First())
            .OrderBy(hit => hit.Kind)
            .ThenBy(hit => hit.DisplayName, StringComparer.Ordinal)
            .ToList();

        var bestKind = perUnit[0].Kind;
        var tiedAtBest = perUnit.Where(hit => hit.Kind == bestKind).ToList();

        if (tiedAtBest.Count > 1)
        {
            return new UnitMatchResult
            {
                InputText = inputText,
                IsAmbiguous = true,
                Alternates = perUnit.Select(ToCandidate).ToList(),
            };
        }

        return new UnitMatchResult
        {
            InputText = inputText,
            Resolved = ToCandidate(perUnit[0]),
            Alternates = perUnit.Skip(1).Select(ToCandidate).ToList(),
        };
    }

    private static UnitMatchCandidate ToCandidate(UnitMatchIndexEntry entry) =>
        new(entry.MeasurementUnitId, entry.DisplayName, entry.Kind);
}
