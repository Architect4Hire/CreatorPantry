using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Tenancy.Managers;

/// <summary>
/// The identity durable background work runs as when no person is acting (auth.md): validated workspace, fixed
/// system membership, least role.
/// </summary>
/// <remarks>
/// Not a <c>WorkspaceMembership</c> row, and never persisted as one. It exists so work that must outlive the
/// person who caused it — staleness marking after an editor has left the workspace — has somewhere to run. The
/// least role is deliberate: a path that needs more than <see cref="WorkspaceRole.Viewer"/> must say why, and
/// system-only moves are authorised by the domain rule that permits them, not by this role. Authorship columns
/// that would hold it stay null instead, as a transition's actor does for a system move.
/// </remarks>
public static class WorkspaceServiceIdentity
{
    public static readonly Guid MembershipId = new("00000000-0000-0000-0000-00000000c0de");

    public const string AccountId = "system:background";

    public const WorkspaceRole Role = WorkspaceRole.Viewer;
}
