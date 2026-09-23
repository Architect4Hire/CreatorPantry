namespace CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;

/// <summary>
/// A named dietary pattern a creator can describe a recipe against — vegan, vegetarian, dairy-free. Global
/// reference data: no <c>WorkspaceId</c>, not <see cref="Tenancy.IWorkspaceOwned"/>, readable before a
/// workspace is resolved (tenancy.md).
/// </summary>
/// <remarks>
/// <para>
/// A profile is a vocabulary entry, not a rule engine and not a promise. It names a pattern and says in
/// <see cref="Description"/> what that pattern covers; whether a given ingredient fits is recorded separately,
/// with evidence, as an <see cref="IngredientDietaryTrait"/>.
/// </para>
/// <para>
/// There is no flag marking a profile medical, certified, or verified, and none may be added. A profile whose
/// stakes are clinical rather than preferential is served by the allergen model and its restrictions
/// (recipes.md), not by a boolean here.
/// </para>
/// </remarks>
public class DietaryProfile
{
    public Guid Id { get; set; }

    /// <summary>Stable lowercase machine key, e.g. <c>vegan</c>. Permanent; seed data resolves by it.</summary>
    public string Code { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Plain-language statement of what this profile includes and excludes. Required: a profile named only
    /// <c>pescatarian</c> leaves each reader to supply a definition, and traits recorded against it would then
    /// be answering different questions.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>A retired profile stays readable so the traits citing it keep their meaning.</summary>
    public bool IsActive { get; set; } = true;
}
