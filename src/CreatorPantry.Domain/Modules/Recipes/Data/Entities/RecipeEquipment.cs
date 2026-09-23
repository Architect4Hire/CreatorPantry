using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Entities;

/// <summary>
/// One piece of equipment a recipe calls for. Interior to the <see cref="Recipe"/> aggregate.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DisplayText"/> carries what the recipe actually needs — "9-inch round cake pan", "12-cup
/// muffin tin" — because the shared <c>EquipmentType</c> vocabulary is deliberately a list of <em>kinds</em>
/// with no capacity, size, or brand. The optional <see cref="EquipmentTypeId"/> is what makes "recipes that
/// need a stand mixer" answerable; it is not where the size lives.
/// </para>
/// <para>
/// Hangs directly off the recipe rather than off a step. A recipe's equipment list is read before cooking
/// starts, which is the whole reason creators publish one.
/// </para>
/// </remarks>
public class RecipeEquipment : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid RecipeId { get; set; }

    /// <summary>Position in the equipment list, unique within the recipe.</summary>
    public int SortOrder { get; set; }

    /// <summary>The creator's wording, including the size and shape the vocabulary cannot hold. Required.</summary>
    public string DisplayText { get; set; } = string.Empty;

    /// <summary>The shared <c>EquipmentType</c> kind, when one was recognised. Additive.</summary>
    public Guid? EquipmentTypeId { get; set; }

    /// <summary>Whether the recipe works without it — a stand mixer where hands would also do.</summary>
    public bool IsOptional { get; set; }

    /// <summary>A creator's aside: what to use instead, why this size matters.</summary>
    public string? Note { get; set; }
}
