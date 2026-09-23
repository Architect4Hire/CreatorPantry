using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Entities;

/// <summary>
/// One heading in a recipe's method — "Make the dough", "Assemble". Interior to the <see cref="Recipe"/>
/// aggregate.
/// </summary>
/// <remarks>
/// Mandatory for the same reason <see cref="RecipeIngredientGroup"/> is: a recipe written as a flat list of
/// steps gets one group with a null <see cref="Title"/>, and every step has exactly one parent. Step
/// numbering that readers see is a presentation concern computed from
/// <see cref="RecipeInstructionStep.SortOrder"/> across groups; it is not stored, because renumbering on
/// every insert would rewrite rows that did not change.
/// </remarks>
public class RecipeInstructionGroup : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid RecipeId { get; set; }

    /// <summary>The heading, or null for the unnamed default group that holds an ungrouped method.</summary>
    public string? Title { get; set; }

    /// <summary>Position among this recipe's instruction groups, unique within the recipe.</summary>
    public int SortOrder { get; set; }

    public ICollection<RecipeInstructionStep> Steps { get; set; } = [];
}
