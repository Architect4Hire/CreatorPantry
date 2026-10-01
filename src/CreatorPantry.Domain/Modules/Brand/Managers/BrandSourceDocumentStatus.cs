namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// Where a source document stands. <c>Active ⇄ Archived → Removed</c>; no state deletes a version or its blob.
/// </summary>
public enum BrandSourceDocumentStatus
{
    Active = 1,

    /// <summary>Out of the pickers and out of default context; still readable, and restorable.</summary>
    Archived = 2,

    /// <summary>
    /// A tombstone. The row and its versions stay so a guide version that cited them still resolves; physical
    /// purge is a retention job's decision, never this state's.
    /// </summary>
    Removed = 3,
}
