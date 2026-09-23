
namespace CreatorPantry.Domain.Managers.Persistence;

/// <summary>
/// The resolved workspace and the caller's active membership for one request or background-job scope.
/// Populated exactly once, by trusted resolution code only (see <see cref="IWorkspaceContextResolver"/>)
/// — never from client input. Reading any member below before resolution throws: there is no
/// <see cref="Guid.Empty"/> or nullable default standing in for "unresolved."
/// </summary>
public interface IWorkspaceContext
{
    /// <summary>True once <see cref="IWorkspaceContextResolver.Resolve"/> has populated this scope.</summary>
    bool IsResolved { get; }

    /// <exception cref="InvalidOperationException">Not yet resolved.</exception>
    Guid WorkspaceId { get; }

    /// <exception cref="InvalidOperationException">Not yet resolved.</exception>
    string WorkspaceSlug { get; }

    /// <summary>The caller's own <c>WorkspaceMembership</c> id, not the workspace's.</summary>
    /// <exception cref="InvalidOperationException">Not yet resolved.</exception>
    Guid MembershipId { get; }

    /// <exception cref="InvalidOperationException">Not yet resolved.</exception>
    WorkspaceRole Role { get; }
}
