using Microsoft.AspNetCore.Identity;

namespace CreatorPantry.Domain.Data;

/// <summary>
/// An authenticated person. Identity answers who the user is; workspace membership answers what they may do.
/// </summary>
public class ApplicationUser : IdentityUser
{
    /// <summary>At most <see cref="Auth.AccountPolicy.DisplayNameMaxLength"/> characters.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>UTC creation instant, set server-side from <c>IClock</c>.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The workspace to open by default. A navigation hint only: it never authorizes access, and every
    /// request re-resolves the workspace from the route and verifies membership.
    /// </summary>
    public Guid? LastWorkspaceId { get; set; }
}
