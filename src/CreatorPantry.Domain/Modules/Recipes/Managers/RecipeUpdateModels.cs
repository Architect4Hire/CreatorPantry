using CreatorPantry.Domain.Modules.Recipes.Data.Entities;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// What an edit did: committed, producing the version that records it — or found the recipe had already
/// moved past the state the edit was composed against, and wrote nothing at all.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A conflict is an outcome, not an exception.</strong> Two creators editing one recipe is the
/// ordinary case; the second one is told to re-read, and nothing about that is a fault. It is also already
/// the shape the layers above speak — Business turns it into an <c>OperationError</c> and the controller
/// into a 409 — whereas an exception escaping the DataLayer would have to be caught somewhere, and the only
/// honest somewhere is the layer that owns the save.
/// </para>
/// <para>
/// A conflict carries nothing, because nothing happened: the transaction rolled back and the recipe is
/// exactly as it was.
/// </para>
/// </remarks>
public sealed class RecipeUpdateOutcome
{
    private RecipeUpdateOutcome(RecipeVersion? version, IReadOnlyList<WorkspaceTag> tags) =>
        (Version, Tags) = (version, tags);

    /// <summary>True when the recipe had moved on and the edit was refused.</summary>
    public bool Conflicted => Version is null;

    /// <summary>
    /// The version this edit wrote, or <c>null</c> when it conflicted.
    /// </summary>
    /// <remarks>
    /// The entity rather than its identity, because it is the recipe's <em>current</em> version the moment
    /// this returns — so the caller can answer with the edited recipe without reading it back to discover
    /// what it just wrote.
    /// </remarks>
    public RecipeVersion? Version { get; }

    /// <summary>
    /// The vocabulary rows the recipe carries after the edit, for naming its tags. Empty on a conflict.
    /// </summary>
    public IReadOnlyList<WorkspaceTag> Tags { get; }

    public static RecipeUpdateOutcome Applied(RecipeVersion version, IReadOnlyList<WorkspaceTag> tags) =>
        new(version, tags);

    public static RecipeUpdateOutcome Conflict() => new(null, []);
}
