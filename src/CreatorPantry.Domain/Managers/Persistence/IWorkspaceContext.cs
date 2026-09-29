namespace CreatorPantry.Domain.Managers.Persistence;

/// <summary>
/// The resolved workspace and the caller's active membership for one request or background-job scope.
/// Populated exactly once, by trusted resolution code only (see <see cref="CreatorPantry.Domain.Modules.Tenancy.IWorkspaceContextResolver"/>)
/// — never from client input. Reading any member below before resolution throws: there is no
/// <see cref="Guid.Empty"/> or nullable default standing in for "unresolved."
/// </summary>
public interface IWorkspaceContext
{
    /// <summary>True once <see cref="CreatorPantry.Domain.Modules.Tenancy.IWorkspaceContextResolver"/> has populated this scope.</summary>
    bool IsResolved { get; }

    /// <exception cref="InvalidOperationException">Not yet resolved.</exception>
    Guid WorkspaceId { get; }

    /// <exception cref="InvalidOperationException">Not yet resolved.</exception>
    string WorkspaceSlug { get; }

    /// <summary>The caller's own <c>WorkspaceMembership</c> id, not the workspace's.</summary>
    /// <exception cref="InvalidOperationException">Not yet resolved.</exception>
    Guid MembershipId { get; }

    /// <summary>
    /// The caller's Identity account id — the <c>WorkspaceMembership.UserId</c> behind
    /// <see cref="MembershipId"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Membership answers what the caller may do here; this answers who they are everywhere.</strong>
    /// The two are deliberately both present: one person in five workspaces has five
    /// <see cref="MembershipId"/> values and one of these, which is why per-account AI usage accounting
    /// (USAGE-001) cannot be derived from membership alone.
    /// </para>
    /// <para>
    /// It carries this type's whole guarantee — populated once by trusted resolution code from a verified
    /// <c>WorkspaceMembership</c> row, never from a request field, a header, or a model. That is what makes
    /// "the account id comes from Identity" structural rather than a convention, and it costs nothing: the
    /// membership row is already read to resolve the workspace.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Not yet resolved.</exception>
    string AccountId { get; }

    /// <exception cref="InvalidOperationException">Not yet resolved.</exception>
    WorkspaceRole Role { get; }
}
