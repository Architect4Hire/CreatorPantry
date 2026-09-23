namespace CreatorPantry.Domain.Managers.Caching;

/// <summary>
/// Builds cache keys for the two data zones (tenancy.md): a workspace-private key always carries the
/// workspace id it is scoped to, and a global platform-reference key never can. This only builds key
/// strings — it does not read, write, or know about a cache store, so it cannot by itself stop an EF entity
/// from being cached under one of these keys; callers cache ServiceModels/DTOs, never EF entities.
/// </summary>
public static class CacheKeys
{
    /// <summary>A workspace-private cache key. <paramref name="workspaceId"/> is mandatory and validated.</summary>
    public static string Workspace(Guid workspaceId, string category, string id)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("A workspace cache key requires a real workspace id.", nameof(workspaceId));
        }

        return $"workspace:{workspaceId:D}:{RequireSegment(category, nameof(category))}:{RequireSegment(id, nameof(id))}";
    }

    /// <summary>A shared platform-reference cache key. There is no overload that accepts a workspace scope.</summary>
    public static string Global(string category, string id) =>
        $"global:{RequireSegment(category, nameof(category))}:{RequireSegment(id, nameof(id))}";

    private static string RequireSegment(string value, string paramName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A cache key segment cannot be empty.", paramName)
            : value;
}
