using CreatorPantry.Domain.Models.DomainModels.Tenancy;
using CreatorPantry.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Data.Repositories;

internal sealed class WorkspaceRepository(CreatorPantryDbContext context) : IWorkspaceRepository
{
    public async Task<WorkspaceMembershipLookup> FindBySlugAsync(string slug, string userId, CancellationToken cancellationToken)
    {
        var workspace = await context.Workspaces.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Slug == slug, cancellationToken);

        if (workspace is null)
        {
            return new WorkspaceMembershipLookup(null, null);
        }

        var membership = await context.WorkspaceMemberships.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.WorkspaceId == workspace.Id && candidate.UserId == userId, cancellationToken);

        return new WorkspaceMembershipLookup(ToSummary(workspace), membership is null ? null : ToSummary(membership));
    }

    public Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken) =>
        context.Workspaces.AsNoTracking().AnyAsync(workspace => workspace.Slug == slug, cancellationToken);

    public async Task<CreatedWorkspace> CreateWithOwnerAsync(
        string name, string slug, string ownerUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = name, Slug = slug, CreatedAt = now };
        var membership = new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            UserId = ownerUserId,
            Role = WorkspaceRole.Owner,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = now,
        };

        // One SaveChangesAsync over both new rows is already one transaction: the creator becomes Owner
        // atomically with the workspace itself, with no explicit BeginTransactionAsync needed.
        context.Workspaces.Add(workspace);
        context.WorkspaceMemberships.Add(membership);
        await context.SaveChangesAsync(cancellationToken);

        return new CreatedWorkspace(ToRecord(workspace), membership.Id, membership.Role);
    }

    public async Task<WorkspaceRecord?> FindByIdAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var workspace = await context.Workspaces.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == workspaceId, cancellationToken);
        return workspace is null ? null : ToRecord(workspace);
    }

    public async Task<WorkspaceRecord> RenameAsync(Guid workspaceId, string name, CancellationToken cancellationToken)
    {
        var workspace = await context.Workspaces.SingleAsync(candidate => candidate.Id == workspaceId, cancellationToken);
        workspace.Name = name;
        await context.SaveChangesAsync(cancellationToken);
        return ToRecord(workspace);
    }

    public async Task<IReadOnlyList<WorkspaceMembershipRow>> FindMembershipsForUserAsync(string userId, CancellationToken cancellationToken)
    {
        var rows = await context.WorkspaceMemberships.AsNoTracking()
            .Where(membership => membership.UserId == userId)
            .Join(context.Workspaces.AsNoTracking(),
                membership => membership.WorkspaceId,
                workspace => workspace.Id,
                (membership, workspace) => new { membership, workspace })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new WorkspaceMembershipRow(ToRecord(row.workspace), row.membership.Id, row.membership.Role, row.membership.Status))
            .ToList();
    }

    private static WorkspaceRecord ToRecord(Workspace workspace) => new(workspace.Id, workspace.Name, workspace.Slug, workspace.CreatedAt);

    private static WorkspaceSummary ToSummary(Workspace workspace) => new(workspace.Id, workspace.Slug);

    private static MembershipSummary ToSummary(WorkspaceMembership membership) => new(membership.Id, membership.Role, membership.Status);
}
