using CreatorPantry.Domain.Modules.Vocabulary.Managers;
namespace CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;

/// <summary>
/// One allergen in the shared vocabulary — milk, egg, peanut, sesame. Global reference data: no
/// <c>WorkspaceId</c>, not <see cref="CreatorPantry.Domain.Managers.Persistence.IWorkspaceOwned"/>, readable before a workspace is resolved
/// (tenancy.md).
/// </summary>
/// <remarks>
/// <para>
/// Names an allergen and nothing more. What any ingredient contains is recorded separately, with evidence and
/// a cited source, as an <see cref="CreatorPantry.Domain.Modules.Ingredients.Data.Entities.IngredientAllergenTrait"/>.
/// </para>
/// <para>
/// Deliberately carries no regulatory classification — no <c>IsMajorAllergen</c>, no declaration-list
/// membership, no jurisdiction. Those are compliance claims that change by country and by year, and stating
/// one here would put CreatorPantry behind a labelling assertion it cannot stand behind (recipes.md). If a
/// jurisdiction's list is ever needed, it belongs in its own cited, dated, sourced table alongside the others.
/// </para>
/// </remarks>
public class Allergen
{
    public Guid Id { get; set; }

    /// <summary>Stable lowercase machine key, e.g. <c>tree-nuts</c>. Permanent; seed data resolves by it.</summary>
    public string Code { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Plain-language statement of what this entry covers, which for allergens is rarely obvious: whether
    /// <c>tree-nuts</c> includes coconut, or <c>milk</c> includes clarified butter, decides what every trait
    /// recorded against it actually means. Required for that reason.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>A retired allergen stays readable so the traits citing it keep their meaning.</summary>
    public bool IsActive { get; set; } = true;
}
