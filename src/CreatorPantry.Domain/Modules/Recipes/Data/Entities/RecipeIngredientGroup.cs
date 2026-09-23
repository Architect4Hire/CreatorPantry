using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Entities;

/// <summary>
/// One heading in a recipe's ingredient list — "For the streusel", "For the glaze". Interior to the
/// <see cref="Recipe"/> aggregate: no lifetime, identity, or repository of its own.
/// </summary>
/// <remarks>
/// <para>
/// Groups are mandatory, not optional. Every ingredient line belongs to exactly one, and a recipe with no
/// headings gets a single group with a null <see cref="Title"/>. The alternative — letting lines hang
/// directly off the recipe when ungrouped — produces two ordering domains that every read has to interleave,
/// and two shapes for every query that touches ingredients.
/// </para>
/// <para>
/// <see cref="WorkspaceId"/> is part of the foreign key to the recipe rather than a column beside it, so a
/// group cannot be attached to a recipe in another workspace.
/// </para>
/// </remarks>
public class RecipeIngredientGroup : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid RecipeId { get; set; }

    /// <summary>The heading, or null for the unnamed default group that holds an ungrouped list.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// Position among this recipe's ingredient groups, unique within the recipe. Reordering therefore writes
    /// in two passes — positions move to a disjoint range first — because SQL Server has no deferred
    /// constraints. That cost buys an ordering that cannot be silently ambiguous.
    /// </summary>
    public int SortOrder { get; set; }

    public ICollection<RecipeIngredient> Ingredients { get; set; } = [];
}
