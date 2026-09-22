namespace CreatorPantry.Domain.Tenancy;

/// <summary>
/// Lifecycle of one <c>WorkspaceMembership</c>. Ordered, not a bitmask. Only <see cref="Active"/> counts
/// as membership for route resolution and authorization.
/// </summary>
public enum WorkspaceMembershipStatus
{
    Invited = 0,
    Active = 10,
    Removed = 20,
}
