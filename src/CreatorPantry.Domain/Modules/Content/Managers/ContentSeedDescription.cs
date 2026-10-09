using System.Globalization;
using System.Text;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// Writes the seed's one-line description from the facets it already holds.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Assembled, not generated.</strong> No model is called and nothing is invented: every word of the
/// output is either a display name taken from the facets or fixed connecting text from this file. That is what
/// makes it safe — a sentence about a recipe that does not exist yet cannot make a claim about food if it only
/// ever names the choices the creator is being handed.
/// </para>
/// <para>
/// It deliberately reads as an instruction to the creator rather than as a description of a dish: "Develop a Thai
/// main course…" is a prompt, where "A delicious Thai main course…" would be a judgement about something nobody
/// has cooked. There are no adjectives here for the same reason.
/// </para>
/// <para>
/// <strong>Every clause has to read for every entry in its catalogue.</strong> The facets are heterogeneous — the
/// occasions run from "weeknight" to "holiday baking", the shot styles from "45-degree angle" to "dark and moody" —
/// so the connecting text is chosen to work for all of them rather than for the tidiest example. An earlier version
/// appended the method as a bare noun and the occasion after "for", which produced "a main course, stir-fry" and
/// "for budget"; "using the … method" and "with … in mind" read for every entry, and the shot is a label because no
/// preposition reads for both an angle and a mood.
/// </para>
/// <para>
/// <strong>A method that carries a safety caution says so here too.</strong> This line is what a creator reads,
/// copies and shares, so the caution cannot live only in the structured
/// <c>ContentSeedMethodServiceModel.RequiresSafetyCaution</c> flag beside it — a client that forgot to render the
/// flag would hand someone "Develop a dessert using the pressure canning method" with nothing attached. The wording
/// is about following guidance, never a claim that anything is safe (recipes.md, ai.md).
/// </para>
/// <para>
/// Every facet is optional, so the line is built from the parts that are present. A seed with nothing but a day
/// still produces a readable sentence.
/// </para>
/// <para>
/// <strong>A seed built around a recipe names the recipe</strong>, and asks for a post about it rather than for a
/// new dish. The cuisine, dish type and method beside the title are then the recipe's own, so they describe it
/// rather than suggest it. The title is creator content, like a theme's name.
/// </para>
/// </remarks>
internal static class ContentSeedDescription
{
    /// <summary>
    /// Appended for a technique flagged <c>RequiresSafetyCaution</c>. Fixed text about process, not a verdict: it
    /// says where to get guidance, and never that the method is safe or that following it makes it so.
    /// </summary>
    internal const string SafetyCaution =
        " Follow tested, authoritative guidance for this method: getting it wrong is a food-safety outcome.";

    public static string For(
        ContentSeedFacetServiceModel? cuisine,
        ContentSeedFacetServiceModel? dishType,
        ContentSeedMethodServiceModel? method,
        ContentSeedFacetServiceModel? photographyStyle,
        ContentSeedFacetServiceModel? channel,
        ContentSeedDayServiceModel day,
        ContentSeedFacetServiceModel? occasion,
        string? recipeTitle = null)
    {
        var line = new StringBuilder();

        // The subject: a cuisine and a dish type read as one noun phrase ("a Thai main course"), and either on
        // its own still reads ("a Thai dish", "a main course").
        var subject = (cuisine, dishType) switch
        {
            ({ } one, { } two) => $"{one.DisplayName} {Lower(two.DisplayName)}",
            ({ } one, null) => $"{one.DisplayName} dish",
            (null, { } two) => Lower(two.DisplayName),
            _ => null,
        };

        if (recipeTitle is null)
        {
            line.Append(CultureInfo.InvariantCulture, $"Develop a {subject ?? "recipe"}");

            if (method is not null)
            {
                line.Append(CultureInfo.InvariantCulture, $" using the {Lower(method.DisplayName)} method");
            }
        }
        else
        {
            // The recipe exists, so the line asks for a post about it rather than a dish to develop. The title is
            // the creator's own, verbatim and in quotation marks so it is plain where their words start and stop;
            // what follows describes the recipe from its own facts: "a Thai main course made using the stir-fry
            // method".
            line.Append(CultureInfo.InvariantCulture, $"Plan a post about \"{recipeTitle}\"");

            if (subject is not null)
            {
                line.Append(CultureInfo.InvariantCulture, $", a {subject}");
            }

            if (method is not null)
            {
                line.Append(subject is null ? "," : string.Empty);
                line.Append(CultureInfo.InvariantCulture, $" made using the {Lower(method.DisplayName)} method");
            }
        }

        if (occasion is not null)
        {
            line.Append(CultureInfo.InvariantCulture, $", with {Lower(occasion.DisplayName)} in mind");
        }

        line.Append('.');

        if (method is { RequiresSafetyCaution: true })
        {
            line.Append(SafetyCaution);
        }

        // A label rather than a clause: no preposition reads for both "45-degree angle" and "dark and moody", and
        // the catalogue's own casing is right after a colon.
        if (photographyStyle is not null)
        {
            line.Append(CultureInfo.InvariantCulture, $" Shot: {photographyStyle.DisplayName}.");
        }

        // The day closes the line, because it is when the work lands rather than what the work is. A theme
        // replaces the bare day name: "for Meat-free Monday" says the day too.
        line.Append(day.Theme is { } theme
            ? $" Publish for {theme.DisplayName}"
            : $" Publish on {day.Day}");

        line.Append(channel is not null ? $" on {channel.DisplayName}." : ".");

        return line.ToString();
    }

    /// <summary>
    /// A display name used mid-sentence, lowercased.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applied to exactly three facets — dish type, method and occasion — and they are all common-noun
    /// vocabularies by design: a meal role, a cooking method, an eating situation. "Main Course" is title-cased for
    /// a picker, not because it is a name, so "a Thai main course" is the correct sentence and "a Thai Main Course"
    /// is not.
    /// </para>
    /// <para>
    /// The facets that are <em>not</em> common nouns never come through here. A cuisine leads the subject, where a
    /// capital is right; a channel follows "on", where "Instagram" and "X" must keep theirs; and a shot style
    /// follows a colon, where the catalogue's own casing reads. That split is why this can be a rule rather than a
    /// guess: an earlier attempt tried to detect proper nouns from inner capitals and got "Stir-Fry" and "Main
    /// Course" wrong, which is what a heuristic in place of a decision buys.
    /// </para>
    /// <para>
    /// The assumption to revisit if one of those three catalogues ever gains a genuine proper noun — a trademarked
    /// method, say. Nothing in them is one today.
    /// </para>
    /// </remarks>
    private static string Lower(string displayName) => displayName.ToLowerInvariant();
}
