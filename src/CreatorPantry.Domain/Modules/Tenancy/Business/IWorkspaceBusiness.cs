using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Tenancy.Managers;

namespace CreatorPantry.Domain.Modules.Tenancy.Business;

/// <summary>Workspace resolution and management rules. Input has already passed shape validation in the facade.</summary>
public interface IWorkspaceBusiness
{
    Task<OperationResult<ResolvedWorkspaceServiceModel>> ResolveAsync(
        string userId, ResolveWorkspaceViewModel model, CancellationToken cancellationToken);

    Task<IReadOnlyList<MyWorkspaceMembershipServiceModel>> GetMyMembershipsAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Creates a workspace and makes the caller its Owner atomically. The slug is derived from the name.</summary>
    Task<OperationResult<WorkspaceServiceModel>> CreateAsync(
        string userId, CreateWorkspaceViewModel model, CancellationToken cancellationToken);

    /// <summary>Reads the workspace already resolved for this scope (<see cref="CreatorPantry.Domain.Managers.Persistence.IWorkspaceContext"/>).</summary>
    Task<WorkspaceServiceModel> GetCurrentAsync(CancellationToken cancellationToken);

    /// <summary>Renames the workspace already resolved for this scope.</summary>
    Task<WorkspaceServiceModel> RenameCurrentAsync(UpdateWorkspaceViewModel model, CancellationToken cancellationToken);

    /// <inheritdoc cref="CreatorPantry.Domain.Modules.Tenancy.Data.IWorkspaceRepository.FindMemberDisplayNamesAsync"/>
    Task<IReadOnlyDictionary<Guid, string>> FindMemberDisplayNamesAsync(
        IReadOnlyCollection<Guid> membershipIds,
        CancellationToken cancellationToken);
}
