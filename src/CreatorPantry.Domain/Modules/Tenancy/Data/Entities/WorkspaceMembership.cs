using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Tenancy.Managers;

namespace CreatorPantry.Domain.Modules.Tenancy.Data.Entities;

/// <summary>
/// Authorization data linking a user to a workspace. Identity answers who the user is; this answers what
/// they may do in one specific workspace. Never an Identity role.
/// </summary>
/// <remarks>
/// The one named exception to <c>WorkspaceOwnershipConvention</c>'s global query filter: querying this
/// entity before a workspace is resolved returns every workspace's rows unfiltered, rather than throwing
/// like every other <see cref="IWorkspaceOwned"/> entity — because resolving a workspace means reading this
/// table first. <see cref="Repositories.WorkspaceRepository"/>'s pre-resolution lookup is the only place
/// this is safe, because it supplies its own explicit <c>WorkspaceId</c>/<c>UserId</c> predicate and never
/// relies on the ambient filter. Any other query against this DbSet — a new repository method, a
/// background job, anything — must do the same, or it will read across workspace boundaries.
/// </remarks>
public class WorkspaceMembership : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public string UserId { get; set; } = string.Empty;

    public WorkspaceRole Role { get; set; }

    public WorkspaceMembershipStatus Status { get; set; }

    public DateTimeOffset JoinedAt { get; set; }
}
