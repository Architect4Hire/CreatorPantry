using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Modules.Measurement.Data.Entities;

/// <summary>
/// One unit of measure in the shared platform vocabulary. Global reference data: it has no
/// <c>WorkspaceId</c>, is not <see cref="CreatorPantry.Domain.Managers.Persistence.IWorkspaceOwned"/>, and is therefore readable before a
/// workspace is resolved (tenancy.md). Creators never own or edit these rows.
/// </summary>
/// <remarks>
/// Recognising a unit enriches a recipe line; it never replaces what the creator typed (recipes.md). A
/// converted or scaled value derived from <see cref="BaseUnitFactor"/> is a presentation or a proposal, not a
/// rewrite of the entered text.
/// </remarks>
public class MeasurementUnit
{
    public Guid Id { get; set; }

    /// <summary>
    /// Stable lowercase machine key, unique across the platform — <c>g</c>, <c>ml</c>, <c>tsp</c>,
    /// <c>floz-us</c>, <c>each</c>. Seed data and future foreign keys resolve a unit by this, so it is
    /// permanent: a renamed unit changes <see cref="DisplayName"/>, never its code.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Singular display form: "gram".</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Plural display form: "grams". Stored rather than derived; pluralization is irregular.</summary>
    public string PluralName { get; set; } = string.Empty;

    /// <summary>Short display form: "g".</summary>
    public string Abbreviation { get; set; } = string.Empty;

    public MeasurementDimension Dimension { get; set; }

    public MeasurementSystem System { get; set; }

    /// <summary>
    /// How many of this dimension's base unit one of this unit equals — <c>1000</c> for a kilogram against
    /// the gram base, <c>4.92892159375</c> for a US teaspoon against the millilitre base. The factor is
    /// always relative to <see cref="MeasurementPolicy.BaseUnitCode"/> for this unit's own
    /// <see cref="Dimension"/>; there is no target-unit column, so a factor structurally cannot bridge two
    /// dimensions.
    /// </summary>
    /// <remarks>
    /// Null exactly when <see cref="MeasurementPolicy.RequiresBaseUnitFactor"/> is false — temperature is
    /// affine rather than multiplicative, and qualitative units have no numeric relationship to convert.
    /// Enforced by <c>CK_MeasurementUnits_Factor_Dimension</c>, and required to be positive by
    /// <c>CK_MeasurementUnits_Factor_Positive</c>.
    /// </remarks>
    public decimal? BaseUnitFactor { get; set; }

    /// <summary>
    /// Decimal places to round to when rendering a quantity in this unit. A display convention only — it says
    /// nothing about how accurately anything was measured, and it is never used to justify a nutrition,
    /// allergen, or food-safety claim.
    /// </summary>
    public int DisplayPrecision { get; set; }

    /// <summary>
    /// Whether the unit is offered for new input. A retired unit stays readable so existing recipes and
    /// versions keep resolving, it simply leaves the pickers.
    /// </summary>
    public bool IsActive { get; set; } = true;
}
