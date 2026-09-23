using CreatorPantry.Domain.Modules.Tenancy.Managers;

namespace CreatorPantry.Domain.Modules.Tenancy.Data;

internal sealed class WorkspaceDataLayer(IWorkspaceRepository repository) : IWorkspaceDataLayer
{
    public Task<WorkspaceMembershipLookup> FindBySlugAsync(string slug, string userId, CancellationToken cancellationToken) =>
        repository.FindBySlugAsync(slug, userId, cancellationToken);

    public Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken) =>
        repository.SlugExistsAsync(slug, cancellationToken);

    public Task<CreatedWorkspace> CreateWithOwnerAsync(
        string name, string slug, string ownerUserId, DateTimeOffset now, CancellationToken cancellationToken) =>
        repository.CreateWithOwnerAsync(name, slug, ownerUserId, now, cancellationToken);

    public Task<WorkspaceRecord?> FindByIdAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        repository.FindByIdAsync(workspaceId, cancellationToken);

    public Task<WorkspaceRecord> RenameAsync(Guid workspaceId, string name, CancellationToken cancellationToken) =>
        repository.RenameAsync(workspaceId, name, cancellationToken);

    public Task<IReadOnlyList<WorkspaceMembershipRow>> FindMembershipsForUserAsync(string userId, CancellationToken cancellationToken) =>
        repository.FindMembershipsForUserAsync(userId, cancellationToken);
}
