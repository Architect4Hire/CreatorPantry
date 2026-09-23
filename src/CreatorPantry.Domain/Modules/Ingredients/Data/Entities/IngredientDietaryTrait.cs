using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Modules.Ingredients.Data.Entities;

/// <summary>
/// What one cited source says about one ingredient's compatibility with one <see cref="DietaryProfile"/>, as
/// of one date. Global reference data: no <c>WorkspaceId</c>, not <see cref="Tenancy.IWorkspaceOwned"/>.
/// </summary>
/// <remarks>
/// <para>
/// Descriptive metadata carrying its own provenance, not a suitability ruling (recipes.md). The absence of a
/// row means unknown and never means compatible.
/// </para>
/// <para>
/// Shaped like <see cref="IngredientAllergenTrait"/> and deliberately kept separate from it rather than folded
/// into one polymorphic trait table. The two answer different questions with different stakes, and their state
/// vocabularies are asymmetric for that reason — a shared table would need a shared enum, and a shared enum
/// would have to contain a member meaning "compatible" that an allergen row could then select.
/// </para>
/// </remarks>
public class IngredientDietaryTrait
{
    public Guid Id { get; set; }

    public Guid IngredientId { get; set; }

    public Guid DietaryProfileId { get; set; }

    /// <summary>Required: provenance is not optional for a reference fact.</summary>
    public Guid ReferenceSourceId { get; set; }

    /// <inheritdoc cref="IngredientAllergenTrait.ReferenceSourceKind"/>
    public ReferenceSourceKind ReferenceSourceKind { get; set; }

    public DietaryCompatibility Compatibility { get; set; }

    /// <summary>
    /// What the source actually stated — "refined with bone char by some producers", "certified by the
    /// manufacturer". Required for <see cref="DietaryCompatibility.DependsOnProduct"/>, which says nothing
    /// useful without it; see <see cref="TraitPolicy.RequiresEvidenceNote(DietaryCompatibility)"/>. Empty
    /// otherwise, never null.
    /// </summary>
    public string EvidenceNote { get; set; } = TraitPolicy.NoEvidenceNote;

    /// <inheritdoc cref="IngredientAllergenTrait.EffectiveFrom"/>
    public DateOnly EffectiveFrom { get; set; }

    public TraitReviewStatus ReviewStatus { get; set; }
}
