using CreatorPantry.Domain.Modules.Vocabulary.Managers;
namespace CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;

/// <summary>
/// A coarse shared grouping for ingredients — "dairy", "baking", "produce". Global reference data: no
/// <c>WorkspaceId</c>, not <see cref="CreatorPantry.Domain.Managers.Persistence.IWorkspaceOwned"/>, readable before a workspace is resolved
/// (tenancy.md).
/// </summary>
/// <remarks>
/// Created here as the minimum <see cref="CreatorPantry.Domain.Modules.Ingredients.Data.Entities.Ingredient"/> needs. The remaining controlled vocabularies
/// (cuisine, course, technique, equipment type) are a separate piece of work and are not modelled yet.
/// </remarks>
public class FoodCategory
{
    public Guid Id { get; set; }

    /// <summary>Stable lowercase machine key; permanent, because seed data resolves a category by it.</summary>
    public string Code { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>A retired category stays readable so existing ingredients keep resolving.</summary>
    public bool IsActive { get; set; } = true;
}
