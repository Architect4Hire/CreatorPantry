using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The unit words a first draft is asked to write units with: the catalogue's own words, kept only where they
/// lead back to their own unit.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> A model may not name a unit id (ai.md), so a draft's units are words, and
/// accepting the draft matches those words to catalogue units deterministically (<c>IngredientUnitResolution</c>
/// in the Recipes module). A word the catalogue does not know stays as written and is flagged for the creator.
/// Showing the model the words that <em>will</em> match is how most of those flags never arise. The match stays
/// the authority; this list only makes it likelier to succeed.
/// </para>
/// <para>
/// <strong>A word is offered only when it resolves to its own unit.</strong> Each candidate — a unit's
/// abbreviation, display name and plural — is run through the same catalogue match, and kept only when it comes
/// back confidently as that unit. A word two units share, or one an alias table has claimed for another unit, is
/// left out, so the list never teaches a word the match would then refuse.
/// </para>
/// <para>
/// <strong>Temperature units are never offered.</strong> An ingredient measured in degrees is refused by the
/// database, and the prompt already says where a temperature belongs.
/// </para>
/// <para>
/// <strong>Platform data, still bounded.</strong> The catalogue has no workspace and no creator can write to it,
/// but it reaches the prompt as a References segment, and every word must look like a unit word — letters,
/// digits, spaces, a dot, an apostrophe or a hyphen, at most <see cref="MaxWordLength"/> characters — or it is
/// left out. A catalogue row that does not look like a unit has no business in a prompt.
/// </para>
/// </remarks>
public static class AiRecipeDraftUnitWords
{
    /// <summary>The longest word offered. The catalogue's longest is well under it.</summary>
    public const int MaxWordLength = 32;

    private static readonly Regex UnitWord = new(
        @"\A[\p{L}\p{N}][\p{L}\p{N} .'-]*\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// The words worth asking the catalogue about: each non-temperature unit's abbreviation, display name and
    /// plural, trimmed, well-formed and distinct, in catalogue order.
    /// </summary>
    public static IReadOnlyList<string> Candidates(IReadOnlyList<MeasurementUnitServiceModel> units)
    {
        ArgumentNullException.ThrowIfNull(units);

        return
        [
            .. units
                .Where(unit => unit.Dimension != MeasurementDimension.Temperature)
                .SelectMany(WordsOf)
                .Distinct(StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Renders the words that resolved to their own unit, one unit per line, grouped by what each measures.
    /// </summary>
    /// <param name="units">The active catalogue, in the order it should be listed.</param>
    /// <param name="candidates">The words asked about, as <see cref="Candidates"/> returned them.</param>
    /// <param name="matches">The catalogue's answer for each candidate, in the same order.</param>
    /// <returns>The rendered list, or <c>null</c> when no word survived — there is then nothing worth showing.</returns>
    public static string? Render(
        IReadOnlyList<MeasurementUnitServiceModel> units,
        IReadOnlyList<string> candidates,
        IReadOnlyList<UnitMatchResult> matches)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(matches);

        if (candidates.Count != matches.Count)
        {
            throw new ArgumentException("Every candidate needs exactly one match result.", nameof(matches));
        }

        var resolvesTo = new Dictionary<string, Guid>(StringComparer.Ordinal);

        for (var index = 0; index < candidates.Count; index++)
        {
            if (!matches[index].IsAmbiguous && matches[index].Resolved is { } resolved)
            {
                resolvesTo[candidates[index]] = resolved.MeasurementUnitId;
            }
        }

        var lines = units
            .Where(unit => unit.Dimension != MeasurementDimension.Temperature)
            .Select(unit => (
                unit.Dimension,
                Words: WordsOf(unit)
                    .Where(word => resolvesTo.TryGetValue(word, out var id) && id == unit.Id)
                    .Distinct(StringComparer.Ordinal)
                    .ToList()))
            .Where(line => line.Words.Count > 0)
            .ToList();

        if (lines.Count == 0)
        {
            return null;
        }

        var rendered = new StringBuilder();
        rendered.AppendLine("Unit words CreatorPantry recognises, one unit per line:");

        foreach (var group in lines.GroupBy(line => line.Dimension).OrderBy(group => group.Key))
        {
            rendered.AppendLine();
            rendered.AppendLine(CultureInfo.InvariantCulture, $"{DimensionLabel(group.Key)}:");

            foreach (var line in group)
            {
                rendered.AppendLine(CultureInfo.InvariantCulture, $"- {string.Join(", ", line.Words)}");
            }
        }

        return rendered.ToString();
    }

    private static IEnumerable<string> WordsOf(MeasurementUnitServiceModel unit) =>
        new[] { unit.Abbreviation, unit.DisplayName, unit.PluralName }
            .Select(word => word?.Trim() ?? string.Empty)
            .Where(word => word.Length is > 0 and <= MaxWordLength && UnitWord.IsMatch(word));

    /// <summary>A fixed phrase per dimension. Never a catalogue string.</summary>
    private static string DimensionLabel(MeasurementDimension dimension) => dimension switch
    {
        MeasurementDimension.Mass => "By weight",
        MeasurementDimension.Volume => "By volume",
        MeasurementDimension.Count => "By count",
        _ => "Other measures",
    };
}
