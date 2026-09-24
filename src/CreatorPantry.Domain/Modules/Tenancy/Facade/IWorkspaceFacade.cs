using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Tenancy.Managers;

namespace CreatorPantry.Domain.Modules.Tenancy.Facade;

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

    /// <summary>
    /// Names the people behind the given memberships of the resolved workspace.
    /// </summary>
    /// <returns>
    /// A display name per membership that exists in this workspace. One that does not is absent rather than
    /// an error — a recorded action outlives the membership that performed it, and the caller decides what an
    /// unnamed actor reads as.
    /// </returns>
    /// <remarks>
    /// The cross-module entry point another module uses to put a human name on something it recorded. Recipes
    /// stores the membership that wrote each version and cannot resolve it itself: memberships and user
    /// display names belong to this module and to Auth, and facade to facade is the only way across.
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, string>> FindMemberDisplayNamesAsync(
        IReadOnlyCollection<Guid> membershipIds,
        CancellationToken cancellationToken);
}
