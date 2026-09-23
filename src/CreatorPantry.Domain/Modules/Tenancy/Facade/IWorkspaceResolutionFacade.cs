using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Tenancy.Managers;

namespace CreatorPantry.Domain.Modules.Tenancy.Facade;

/// <summary>
/// Application boundary for workspace resolution: a route slug plus the authenticated caller becomes a
/// workspace id and confirmed active membership, or one indistinguishable not-found failure. Used by the
/// (not yet built) route resolution middleware, and reusable by background work and AI plugins.
/// </summary>
public interface IWorkspaceResolutionFacade
{
    /// <param name="userId">The authenticated caller's id. Never taken from request input.</param>
    Task<OperationResult<ResolvedWorkspaceServiceModel>> ResolveAsync(
        string userId, ResolveWorkspaceViewModel model, CancellationToken cancellationToken);
}
