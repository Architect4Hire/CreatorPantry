using CreatorPantry.Domain.Data.Tenancy;
using CreatorPantry.Domain.Models.DomainModels.Tenancy;
using CreatorPantry.Domain.Tenancy;

namespace CreatorPantry.Tests.Tenancy;

internal sealed class FakeWorkspaceDataLayer : IWorkspaceDataLayer
{
    public WorkspaceMembershipLookup Lookup { get; set; } = new(null, null);

    public List<(string Slug, string UserId)> Calls { get; } = [];

    /// <summary>Slugs <see cref="SlugExistsAsync"/> reports as already taken.</summary>
    public HashSet<string> ExistingSlugs { get; } = [];

    public List<string> SlugExistsCalls { get; } = [];

    public List<(string Name, string Slug, string OwnerUserId, DateTimeOffset Now)> CreateCalls { get; } = [];

    public IReadOnlyList<WorkspaceMembershipRow> Memberships { get; set; } = [];

    public WorkspaceRecord? WorkspaceById { get; set; }

    public List<(Guid WorkspaceId, string Name)> RenameCalls { get; } = [];

    public Task<WorkspaceMembershipLookup> FindBySlugAsync(string slug, string userId, CancellationToken cancellationToken)
    {
        Calls.Add((slug, userId));
        return Task.FromResult(Lookup);
    }

    public Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken)
    {
        SlugExistsCalls.Add(slug);
        return Task.FromResult(ExistingSlugs.Contains(slug));
    }

    public Task<CreatedWorkspace> CreateWithOwnerAsync(
        string name, string slug, string ownerUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        CreateCalls.Add((name, slug, ownerUserId, now));
        return Task.FromResult(new CreatedWorkspace(new WorkspaceRecord(Guid.NewGuid(), name, slug, now), Guid.NewGuid(), WorkspaceRole.Owner));
    }

    public Task<WorkspaceRecord?> FindByIdAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        Task.FromResult(WorkspaceById);

    public Task<WorkspaceRecord> RenameAsync(Guid workspaceId, string name, CancellationToken cancellationToken)
    {
        RenameCalls.Add((workspaceId, name));
        return Task.FromResult(new WorkspaceRecord(workspaceId, name, "renamed-workspace", DateTimeOffset.UtcNow));
    }

    public Task<IReadOnlyList<WorkspaceMembershipRow>> FindMembershipsForUserAsync(string userId, CancellationToken cancellationToken) =>
        Task.FromResult(Memberships);
}
