namespace CreatorPantry.Domain.Auth;

/// <summary>
/// Platform-wide ASP.NET Core Identity roles. Workspace roles (Viewer, Contributor, Editor, Owner) are
/// <c>WorkspaceMembership</c> data, never Identity roles. PlatformAdmin does not imply workspace access.
/// </summary>
public static class PlatformRoles
{
    public const string PlatformAdmin = "PlatformAdmin";

    /// <summary>Every Identity role the platform seeds. Exactly one.</summary>
    public static IReadOnlyList<string> All { get; } = [PlatformAdmin];
}
