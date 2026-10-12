using System.Globalization;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// What a creative-context package's recipes state, as the claim scanners check generated text against it.
/// </summary>
/// <remarks>
/// One definition for every capability that writes from a package — posts (AF.6.3) and the long-form outline
/// (LF.2.1) — so "is this figure the recipe's" has one answer.
/// </remarks>
internal static class CreativeContextClaimSource
{
    /// <summary>
    /// What the claim scanners find in one piece of text: an unsupported figure, a safety, allergen, dietary or
    /// health claim, storage advice, invented provenance, and a search metric.
    /// </summary>
    /// <remarks>
    /// Findings, not refusals. With no recipe in the package there is nothing for a figure to agree with, so
    /// the figure check is skipped and every check that needs no source still runs: with no recipe, storage
    /// advice and a family story are unsupported by definition.
    /// </remarks>
    public static IEnumerable<(AiWarningKind Kind, string Code, string Message)> Claims(
        string text, AiEditorialSourceFacts? source) =>
        (source is null
            ? AiEditorialClaimScanner.ScanTextWithoutSource(text)
            : AiEditorialClaimScanner.ScanText(text, source))
        .Concat(AiSeoClaimScanner.ScanTextForMetrics(text));

    /// <summary>
    /// What the package's recipes state, for a claim to be checked against; null when it carries no recipe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Figures are taken from the recipe alone: its lines, yield, recorded times and title. Support for a
    /// <em>claim</em> is taken only from titles — the creator's own naming of the dish and of the piece — never
    /// from an ingredient line or a step, which is the scanner's own rule: "plain flour" in a list does not
    /// make a post saying "gluten-free" true, while a recipe titled "Gluten-Free Brownies" may be called that.
    /// </para>
    /// <para>
    /// <strong>The working title supports a claim and never a figure.</strong> It is a label on the piece of
    /// work, not a recipe fact, so "Ready in 10 Minutes" as a working title does not make ten minutes a time
    /// the recipe states. It is therefore added to the prose after the figures have been read.
    /// </para>
    /// </remarks>
    public static AiEditorialSourceFacts? Facts(CreativeContextPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (package.Recipes.Count == 0)
        {
            return null;
        }

        var figures = new List<string>();

        foreach (var recipe in package.Recipes)
        {
            figures.AddRange(recipe.Ingredients);
            figures.AddRange(recipe.Steps);

            if (recipe.YieldText is { } yield)
            {
                figures.Add(yield);
            }

            foreach (var minutes in new[] { recipe.PrepTimeMinutes, recipe.CookTimeMinutes, recipe.RestTimeMinutes, recipe.TotalTimeMinutes })
            {
                if (minutes is { } value)
                {
                    figures.Add($"{value.ToString(CultureInfo.InvariantCulture)} minutes");
                }
            }
        }

        var recipeTitles = string.Join(' ', package.Recipes.Select(recipe => recipe.Title));
        var facts = AiEditorialSourceFacts.Of(figures, recipeTitles, storageNotes: null);

        return package.Words?.WorkingTitle is { } workingTitle
            ? facts with { Prose = $"{recipeTitles} {workingTitle}" }
            : facts;
    }
}
