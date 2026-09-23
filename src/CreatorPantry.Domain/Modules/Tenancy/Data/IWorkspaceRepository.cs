using CreatorPantry.Domain.Modules.Tenancy.Managers;

namespace CreatorPantry.Domain.Modules.Tenancy.Data;

public interface IWorkspaceRepository
{
    /// <summary>
    /// Looks up a workspace by slug and the given user's membership in it, if either exists. Returns raw
    /// truth only: disclosure policy (collapsing unknown/inaccessible into one outcome) is Business's job.
    /// </summary>
    Task<WorkspaceMembershipLookup> FindBySlugAsync(string slug, string userId, CancellationToken cancellationToken);

    Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken);

    /// <summary>Atomically inserts a new <c>Workspace</c> and its creator's <c>Owner</c> membership.</summary>
    Task<CreatedWorkspace> CreateWithOwnerAsync(
        string name, string slug, string ownerUserId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<WorkspaceRecord?> FindByIdAsync(Guid workspaceId, CancellationToken cancellationToken);

    Task<WorkspaceRecord> RenameAsync(Guid workspaceId, string name, CancellationToken cancellationToken);

    /// <summary>
    /// Every workspace membership the given user holds, joined with each workspace's own fields. Queried by
    /// an explicit <c>UserId</c> predicate — the same pre-resolution-safe pattern as <see cref="FindBySlugAsync"/>,
    /// since this is used by <c>GET /api/v1/me</c> before any single workspace is resolved.
    /// </summary>
    Task<IReadOnlyList<WorkspaceMembershipRow>> FindMembershipsForUserAsync(string userId, CancellationToken cancellationToken);
}
