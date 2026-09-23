namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The editorial facts about a version a write is about to capture — why it exists and what it claims about
/// itself.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is Business's decision. The DataLayer supplies only what is mechanical: the version
/// number (1 on a create, one past the current version on an edit), the parent version, the concurrency
/// token the edit was composed against, and the timestamp and actor, which are copied from the recipe so
/// the two rows cannot disagree about when it happened or who did it.
/// </para>
/// <para>
/// One type for both writes rather than one per write. The distinction between a first version and a later
/// one is entirely in those mechanical values, none of which a caller supplies, so a second record would
/// have had the same three members and invited them to drift.
/// </para>
/// </remarks>
public sealed record RecipeVersionFacts(
    RecipeVersionSource Source,
    RecipeVersionReadiness Readiness,
    string? Reason);
