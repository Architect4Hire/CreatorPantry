using CreatorPantry.Domain.Managers.Persistence;
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

    /// <summary>
    /// Resolves a workspace/membership pair a background worker already trusts from server-resolved state — a
    /// queued <c>AiOperation</c>'s own <c>WorkspaceId</c> and <c>RequestedByMembershipId</c> — rather than from
    /// a route slug and an authenticated user. No shape validation: both arguments are Guids read from internal
    /// state, never client input.
    /// </summary>
    /// <remarks>
    /// <strong>Also populates the caller's ambient <see cref="IWorkspaceContext"/>, unlike <see cref="ResolveAsync"/>.</strong>
    /// That method leaves resolving the context to its own caller (<c>WorkspaceResolutionMiddleware</c>), which
    /// can do so because it lives in the same process boundary as the route. A worker calling this from another
    /// module has no such route to sit behind, and <see cref="IWorkspaceContextResolver"/> is not itself a
    /// facade type another module may cross to reach — so this method resolves the context itself as part of
    /// succeeding. Callable once per scope, like the resolver it wraps.
    /// </remarks>
    Task<OperationResult<ResolvedWorkspaceServiceModel>> ResolveForOperationAsync(
        Guid workspaceId, Guid membershipId, CancellationToken cancellationToken);
}
