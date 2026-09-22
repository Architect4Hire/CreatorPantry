using CreatorPantry.Domain.Models.Results;
using CreatorPantry.Domain.Models.ServiceModels.Tenancy;
using CreatorPantry.Domain.Models.ViewModels.Tenancy;

namespace CreatorPantry.Domain.Facade.Tenancy;

/// <summary>
/// Application boundary for workspace management: creating a workspace, reading/renaming the one already
/// resolved for this scope, and listing the caller's own memberships. Distinct from
/// <see cref="IWorkspaceResolutionFacade"/>, which turns a route slug into a resolved context.
/// </summary>
public interface IWorkspaceFacade
{
    /// <param name="userId">The authenticated caller's id. Never taken from request input.</param>
    Task<IReadOnlyList<MyWorkspaceMembershipServiceModel>> GetMyMembershipsAsync(string userId, CancellationToken cancellationToken);

    /// <param name="userId">The authenticated caller's id. Becomes the new workspace's Owner.</param>
    Task<OperationResult<WorkspaceServiceModel>> CreateAsync(
        string userId, CreateWorkspaceViewModel model, CancellationToken cancellationToken);

    /// <summary>Reads the workspace already resolved for this scope.</summary>
    Task<WorkspaceServiceModel> GetCurrentAsync(CancellationToken cancellationToken);

    /// <summary>Renames the workspace already resolved for this scope.</summary>
    Task<OperationResult<WorkspaceServiceModel>> RenameCurrentAsync(UpdateWorkspaceViewModel model, CancellationToken cancellationToken);
}
