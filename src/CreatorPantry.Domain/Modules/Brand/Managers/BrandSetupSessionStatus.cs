namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>Where a "Create my voice" setup session stands.</summary>
public enum BrandSetupSessionStatus
{
    InProgress = 1,

    /// <summary>
    /// The wizard reached its last step and was marked done. Completing activates nothing: the style guide
    /// versions the creator saved stay the source of truth, and this row is only the record that the walk
    /// through finished.
    /// </summary>
    Completed = 2,
}
