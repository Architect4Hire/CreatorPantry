using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
namespace CreatorPantry.Domain.Modules.Tenancy;

/// <summary>
/// One scope's workspace context: unresolved until <see cref="Resolve"/> runs, then immutable. Register
/// as Scoped and expose the same instance as both <see cref="CreatorPantry.Domain.Managers.Persistence.IWorkspaceContext"/> (read, to nearly
/// everything) and <see cref="CreatorPantry.Domain.Modules.Tenancy.IWorkspaceContextResolver"/> (write, to resolution code only).
/// </summary>
internal sealed class WorkspaceContext : IWorkspaceContext, IWorkspaceContextResolver
{
    private ResolvedWorkspace? _resolved;

    public bool IsResolved => _resolved is not null;

    public Guid WorkspaceId => Require().WorkspaceId;

    public string WorkspaceSlug => Require().WorkspaceSlug;

    public Guid MembershipId => Require().MembershipId;

    public WorkspaceRole Role => Require().Role;

    public void Resolve(Guid workspaceId, string workspaceSlug, Guid membershipId, WorkspaceRole role)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("A resolved workspace id must not be empty.", nameof(workspaceId));
        }

        if (string.IsNullOrWhiteSpace(workspaceSlug))
        {
            throw new ArgumentException("A resolved workspace slug must not be empty.", nameof(workspaceSlug));
        }

        if (membershipId == Guid.Empty)
        {
            throw new ArgumentException("A resolved membership id must not be empty.", nameof(membershipId));
        }

        if (_resolved is not null)
        {
            throw new InvalidOperationException("This workspace context was already resolved.");
        }

        _resolved = new ResolvedWorkspace(workspaceId, workspaceSlug, membershipId, role);
    }

    private ResolvedWorkspace Require() =>
        _resolved ?? throw new InvalidOperationException("Workspace context has not been resolved.");

    private sealed record ResolvedWorkspace(Guid WorkspaceId, string WorkspaceSlug, Guid MembershipId, WorkspaceRole Role);
}
