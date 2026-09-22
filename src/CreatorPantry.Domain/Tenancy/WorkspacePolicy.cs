namespace CreatorPantry.Domain.Tenancy;

/// <summary>Workspace and membership limits shared by request validation and EF configuration.</summary>
public static class WorkspacePolicy
{
    public const int NameMaxLength = 100;

    /// <summary>Lowercase letters, digits, and hyphens; matches the route segment used to resolve a workspace.</summary>
    public const int SlugMaxLength = 100;

    /// <summary>Lowercase alphanumeric segments joined by single hyphens; no leading, trailing, or doubled hyphen.</summary>
    public const string SlugPattern = "^[a-z0-9]+(-[a-z0-9]+)*$";
}
