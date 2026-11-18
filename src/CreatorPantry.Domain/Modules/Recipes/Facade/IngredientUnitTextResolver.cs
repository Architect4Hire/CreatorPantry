using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Measurement.Facade;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Facade;

/// <summary>
/// The catalogue unit each free-text unit word is recognised as — the one answer to "is this a CreatorPantry
/// unit?" that accepting a draft, accepting a proposed change and the draft review screen all share.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One copy, because three would drift.</strong> The review screen flags a unit word before a draft is
/// accepted, and acceptance records the unit afterwards. If the two asked different questions, a word the review
/// passed could arrive unrecognised, or one it flagged could arrive matched — and a flag that disagrees with the
/// recipe it becomes is worse than none.
/// </para>
/// <para>
/// The match is the Measurement catalogue's own exact/alias match (<see cref="IMeasurementFacade.ResolveCandidatesAsync"/>),
/// and which matches an ingredient line may record is <see cref="IngredientUnitResolution"/>'s: ambiguous text, a
/// temperature unit and a retired unit are all left unrecognised rather than guessed.
/// </para>
/// <para>
/// <strong>It never fails the caller.</strong> Recognising a unit enriches a line; it is not what makes a line
/// valid. If the catalogue cannot answer, every word is unrecognised — which the editor and the readiness
/// checklist then flag — rather than a creator losing an accepted draft over a lookup.
/// </para>
/// </remarks>
internal interface IIngredientUnitTextResolver
{
    /// <summary>The recognised unit for each distinct trimmed word, keyed by <see cref="IngredientUnitResolution.Key"/>.</summary>
    Task<IReadOnlyDictionary<string, ResolvedIngredientUnit>> ResolveAsync(
        IEnumerable<string?> unitTexts, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IIngredientUnitTextResolver"/>
internal sealed class IngredientUnitTextResolver(IMeasurementFacade measurement) : IIngredientUnitTextResolver
{
    private static readonly IReadOnlyDictionary<string, ResolvedIngredientUnit> NoResolvedUnits =
        new Dictionary<string, ResolvedIngredientUnit>(StringComparer.Ordinal);

    public async Task<IReadOnlyDictionary<string, ResolvedIngredientUnit>> ResolveAsync(
        IEnumerable<string?> unitTexts, CancellationToken cancellationToken)
    {
        var candidates = IngredientUnitResolution.Candidates(unitTexts);
        if (candidates.Count == 0)
        {
            return NoResolvedUnits;
        }

        var matches = new List<UnitMatchResult>(candidates.Count);

        foreach (var chunk in candidates.Chunk(ReferencePolicy.MaxMatchCandidates))
        {
            var answer = await measurement.ResolveCandidatesAsync(chunk, cancellationToken);
            if (!answer.Succeeded)
            {
                return NoResolvedUnits;
            }

            matches.AddRange(answer.Value!);
        }

        var unitIds = matches
            .Where(match => !match.IsAmbiguous && match.Resolved is not null)
            .Select(match => match.Resolved!.MeasurementUnitId)
            .Distinct()
            .ToList();

        var usableUnits = unitIds.Count == 0
            ? new Dictionary<Guid, MeasurementUnitServiceModel>()
            : (await measurement.FindUnitsByIdsAsync(unitIds, cancellationToken)).ToDictionary(unit => unit.Id);

        return IngredientUnitResolution.Select(candidates, matches, usableUnits);
    }
}
