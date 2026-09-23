using CreatorPantry.Domain.Modules.Tenancy.Managers;

namespace CreatorPantry.Domain.Modules.Tenancy.Data;

public interface IWorkspaceDataLayer
{
    Task<WorkspaceMembershipLookup> FindBySlugAsync(string slug, string userId, CancellationToken cancellationToken);

    Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken);

    Task<CreatedWorkspace> CreateWithOwnerAsync(
        string name, string slug, string ownerUserId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<WorkspaceRecord?> FindByIdAsync(Guid workspaceId, CancellationToken cancellationToken);

    Task<WorkspaceRecord> RenameAsync(Guid workspaceId, string name, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkspaceMembershipRow>> FindMembershipsForUserAsync(string userId, CancellationToken cancellationToken);
}
