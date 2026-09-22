namespace CreatorPantry.Domain.Models.ServiceModels.Auth;

/// <summary>
/// What the gateway needs to hold a session. Internal only: returned to the gateway's service identity and
/// never to a browser. The security stamp lets the gateway detect password changes and resets.
/// </summary>
public sealed record SessionUserServiceModel(
    string UserId, string DisplayName, IReadOnlyList<string> Roles, string SecurityStamp);
