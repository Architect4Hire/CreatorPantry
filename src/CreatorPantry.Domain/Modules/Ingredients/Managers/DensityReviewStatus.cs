using CreatorPantry.Domain.Managers.Reference;
namespace CreatorPantry.Domain.Modules.Ingredients.Managers;

/// <summary>
/// How much trust a <see cref="CreatorPantry.Domain.Modules.Ingredients.Data.Entities.IngredientDensityReference"/> has earned. Only
/// <see cref="Approved"/> rows may drive a mass-volume conversion; everything else is on record without
/// being actionable.
/// </summary>
/// <remarks>
/// A state, not a ranking — compare with equality, never with <c>&gt;=</c>. These numeric values are
/// persisted and named literally by <c>CK_IngredientDensityReferences_AiEstimate_NotApproved</c>, so they
/// must not be renumbered. Values leave gaps so a state can be inserted without renumbering.
/// </remarks>
public enum DensityReviewStatus
{
    /// <summary>Recorded but not yet checked. The default for imported and contributed values.</summary>
    Unreviewed = 0,

    /// <summary>Checked against its source and cleared for use in conversions.</summary>
    Approved = 10,

    /// <summary>Checked and found wrong or unusable. Kept so the same bad value is not re-imported.</summary>
    Rejected = 20,

    /// <summary>Replaced by a later value for the same ingredient, condition, and source.</summary>
    Superseded = 30,
}
