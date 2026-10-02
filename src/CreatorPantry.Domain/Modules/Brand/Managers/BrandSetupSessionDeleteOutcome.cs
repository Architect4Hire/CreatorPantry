namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>How a "start over" delete ended.</summary>
public enum BrandSetupSessionDeleteOutcome
{
    /// <summary>The caller's session row was deleted.</summary>
    Deleted = 1,

    /// <summary>There was no session to delete. Still a success: delete is idempotent.</summary>
    NothingToDelete = 2,

    /// <summary>The row kept changing under the delete and is still there. Nothing was removed.</summary>
    Conflict = 3,
}
