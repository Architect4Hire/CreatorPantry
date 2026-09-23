namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Whether a recipe ingredient line has been matched against the shared ingredient vocabulary, and what
/// happened when it was.
/// </summary>
/// <remarks>
/// <para>
/// A null <c>IngredientId</c> alone cannot distinguish "nobody has looked at this line yet" from "we looked
/// and found nothing", and the two need different interfaces: the first is silent, the second is a prompt to
/// the creator. The <see cref="CreatorPantry.Domain.Modules.Ingredients.Data.Entities.Ingredient"/> catalogue's own
/// documentation requires that an unmatched line is surfaced as unresolved rather than guessed, which is
/// only possible if the system can tell that it tried.
/// </para>
/// <para>
/// This describes the <em>reference</em>, never the line. A line with no match is complete and correct
/// recipe content — its <c>DisplayText</c> is canonical either way (recipes.md), and nothing here downgrades
/// it.
/// </para>
/// </remarks>
public enum IngredientMatchStatus
{
    /// <summary>No match has been attempted. The default for a line the creator has just typed.</summary>
    NotAttempted = 0,

    /// <summary>A single ingredient was matched, and <c>IngredientId</c> names it.</summary>
    Matched = 1,

    /// <summary>A match was attempted and nothing in the vocabulary fit. <c>IngredientId</c> stays null.</summary>
    NoMatch = 2,

    /// <summary>
    /// Several ingredients fit and the system will not choose between them. <c>IngredientId</c> stays null;
    /// the creator resolves it.
    /// </summary>
    Ambiguous = 3,
}
