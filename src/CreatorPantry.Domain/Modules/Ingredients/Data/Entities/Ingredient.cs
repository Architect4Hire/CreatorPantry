using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Modules.Ingredients.Data.Entities;

/// <summary>
/// One ingredient in the shared platform vocabulary. Global reference data: no <c>WorkspaceId</c>, not
/// <see cref="CreatorPantry.Domain.Managers.Persistence.IWorkspaceOwned"/>, readable before a workspace is resolved (tenancy.md). Creators
/// neither own nor edit these rows.
/// </summary>
/// <remarks>
/// This is a reference, not a replacement. Recognising an ingredient enriches a recipe line while the
/// creator's entered text remains the canonical wording (recipes.md); an unmatched line is surfaced as
/// unresolved rather than guessed. No nutrition, allergen, dietary, density, or embedding data lives here —
/// each of those carries its own provenance and uncertainty model and is modelled separately.
/// </remarks>
public class Ingredient
{
    public Guid Id { get; set; }

    /// <summary>The preferred display form: "all-purpose flour".</summary>
    public string CanonicalName { get; set; } = string.Empty;

    /// <summary>
    /// The natural key, from <see cref="NameNormalization.NormalizeName"/>. Unique platform-wide, which is what
    /// makes "Flour", "flour", and "FLOUR" one ingredient rather than three.
    /// </summary>
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>
    /// Denormalized match text built by <see cref="NameNormalization.BuildSearchText"/> from this name and every
    /// alias, so a lookup can filter one column instead of joining. Derived data: the alias rows remain the
    /// source of truth, and this must be rebuilt whenever the name or the aliases change.
    /// </summary>
    public string SearchText { get; set; } = string.Empty;

    /// <summary>The coarse shared grouping, or null when uncategorized.</summary>
    public Guid? FoodCategoryId { get; set; }

    /// <summary>
    /// The unit to suggest when a creator names this ingredient with a bare number and no unit — "2 garlic"
    /// proposing "2 cloves garlic". A reference to the real <see cref="CreatorPantry.Domain.Modules.Measurement.Data.Entities.MeasurementUnit"/> rather than free
    /// text, so the suggested quantity scales and converts deterministically like any other count.
    /// </summary>
    /// <remarks>
    /// A suggestion only. It never overrides a unit the creator actually wrote.
    /// </remarks>
    public Guid? DefaultCountUnitId { get; set; }

    /// <summary>
    /// Always <see cref="MeasurementDimension.Count"/> when <see cref="DefaultCountUnitId"/> is set, and null
    /// when it is not.
    /// </summary>
    /// <remarks>
    /// Redundant by design. Carrying the dimension here lets the composite foreign key
    /// <c>(DefaultCountUnitId, DefaultCountUnitDimension)</c> point at <c>MeasurementUnits (Id, Dimension)</c>,
    /// which together with <c>CK_Ingredients_DefaultCountUnit_Dimension</c> makes "a default count unit must be
    /// a Count unit" a fact the database enforces — rather than a convention that holds until someone seeds a
    /// mass unit here by mistake.
    /// </remarks>
    public MeasurementDimension? DefaultCountUnitDimension { get; set; }

    /// <summary>
    /// Whether the ingredient is offered for new input. A retired row stays readable so existing recipes and
    /// versions keep resolving; it simply leaves the pickers.
    /// </summary>
    public bool IsActive { get; set; } = true;
}
