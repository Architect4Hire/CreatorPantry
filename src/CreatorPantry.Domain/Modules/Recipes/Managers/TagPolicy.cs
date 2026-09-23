namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Limits for the creator-defined tag vocabulary, shared by request validation and EF configuration.
/// </summary>
public static class TagPolicy
{
    /// <summary>
    /// A tag is a label, not a sentence. Short enough that the pickers and chips stay readable, long enough
    /// for the compound phrases creators actually use ("make ahead for the freezer").
    /// </summary>
    public const int NameMaxLength = 64;

    /// <summary>
    /// How many tags one recipe may carry. A cap rather than a judgement about tagging style: without one, a
    /// single request can attach unbounded rows, and an import can turn one recipe into thousands of them.
    /// </summary>
    public const int MaxTagsPerRecipe = 25;
}
