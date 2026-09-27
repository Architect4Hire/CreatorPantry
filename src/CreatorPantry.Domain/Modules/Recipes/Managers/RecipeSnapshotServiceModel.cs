namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The archived content of one exact, explicitly named recipe version, for a caller — an AI worker computing a
/// diff against a pinned source — that has no use for the recipe as it currently stands.
/// </summary>
/// <param name="VersionNumber">
/// The version's own number, for provenance and messages. The caller pinned the version by id; this is what a
/// creator would recognise it by.
/// </param>
/// <param name="Document">The version's full content, already deserialized.</param>
public sealed record RecipeSnapshotServiceModel(int VersionNumber, RecipeSnapshotDocument Document);
