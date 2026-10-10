using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Tenancy.Managers;

namespace CreatorPantry.Domain.Modules.Tenancy.Business;

/// <summary>Workspace resolution and management rules. Input has already passed shape validation in the facade.</summary>
public interface IWorkspaceBusiness
{
    Task<OperationResult<ResolvedWorkspaceServiceModel>> ResolveAsync(
        string userId, ResolveWorkspaceViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a workspace context for a background worker finishing work a creator already queued, keyed by
    /// the exact membership id recorded on that request rather than by the caller's own identity.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ResolveAsync"/>, the membership need not be <see cref="WorkspaceMembershipStatus.Active"/>:
    /// that rule is about route resolution and authorization for a live actor, and this worker is completing
    /// work a legitimately-active creator already queued, not authorizing a new action on their behalf. Nothing
    /// on the path this feeds re-checks the resolved role — only accepting a proposal does, and that is
    /// re-gated independently at the HTTP disposition boundary.
    /// </remarks>
    Task<OperationResult<ResolvedWorkspaceServiceModel>> ResolveForOperationAsync(
        Guid workspaceId, Guid membershipId, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a workspace for background work that must not depend on any person still being a member: the
    /// workspace has to exist, and the work runs as <see cref="WorkspaceServiceIdentity"/>. Not found is the one
    /// failure, and means the workspace is gone.
    /// </summary>
    Task<OperationResult<ResolvedWorkspaceServiceModel>> ResolveForServiceAsync(
        Guid workspaceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<MyWorkspaceMembershipServiceModel>> GetMyMembershipsAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Creates a workspace and makes the caller its Owner atomically. The slug is derived from the name.</summary>
    Task<OperationResult<WorkspaceServiceModel>> CreateAsync(
        string userId, CreateWorkspaceViewModel model, CancellationToken cancellationToken);

    /// <summary>Reads the workspace already resolved for this scope (<see cref="CreatorPantry.Domain.Managers.Persistence.IWorkspaceContext"/>).</summary>
    Task<WorkspaceServiceModel> GetCurrentAsync(CancellationToken cancellationToken);

    /// <summary>Renames the workspace already resolved for this scope.</summary>
    Task<WorkspaceServiceModel> RenameCurrentAsync(UpdateWorkspaceViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the default measurement system of the workspace already resolved for this scope. Refuses anything
    /// but metric or US customary (B-08).
    /// </summary>
    Task<OperationResult<WorkspaceServiceModel>> SetMeasurementPreferenceCurrentAsync(
        SetMeasurementPreferenceViewModel model, CancellationToken cancellationToken);

    /// <inheritdoc cref="CreatorPantry.Domain.Modules.Tenancy.Data.IWorkspaceRepository.FindMemberDisplayNamesAsync"/>
    Task<IReadOnlyDictionary<Guid, string>> FindMemberDisplayNamesAsync(
        IReadOnlyCollection<Guid> membershipIds,
        CancellationToken cancellationToken);
}
