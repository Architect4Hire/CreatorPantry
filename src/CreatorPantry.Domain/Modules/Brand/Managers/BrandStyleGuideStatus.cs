namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// Where a style guide stands as a whole. Not a version's review state: a version is a draft until a
/// <c>BrandStyleGuideApproval</c> exists for it, and that is recorded, not stored here.
/// </summary>
public enum BrandStyleGuideStatus
{
    Active = 1,

    /// <summary>Out of the pickers; every version stays readable, and the guide is restorable.</summary>
    Archived = 2,
}
