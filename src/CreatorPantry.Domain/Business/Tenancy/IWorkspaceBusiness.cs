using CreatorPantry.Domain.Models.Results;
using CreatorPantry.Domain.Models.ServiceModels.Tenancy;
using CreatorPantry.Domain.Models.ViewModels.Tenancy;

namespace CreatorPantry.Domain.Business.Tenancy;

/// <summary>Workspace resolution and management rules. Input has already passed shape validation in the facade.</summary>
public interface IWorkspaceBusiness
{
    Task<OperationResult<ResolvedWorkspaceServiceModel>> ResolveAsync(
        string userId, ResolveWorkspaceViewModel model, CancellationToken cancellationToken);

    Task<IReadOnlyList<MyWorkspaceMembershipServiceModel>> GetMyMembershipsAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Creates a workspace and makes the caller its Owner atomically. The slug is derived from the name.</summary>
    Task<OperationResult<WorkspaceServiceModel>> CreateAsync(
        string userId, CreateWorkspaceViewModel model, CancellationToken cancellationToken);

    /// <summary>Reads the workspace already resolved for this scope (<see cref="Tenancy.IWorkspaceContext"/>).</summary>
    Task<WorkspaceServiceModel> GetCurrentAsync(CancellationToken cancellationToken);

    /// <summary>Renames the workspace already resolved for this scope.</summary>
    Task<WorkspaceServiceModel> RenameCurrentAsync(UpdateWorkspaceViewModel model, CancellationToken cancellationToken);
}
