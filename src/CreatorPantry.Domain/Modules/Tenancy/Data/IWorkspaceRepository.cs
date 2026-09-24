using CreatorPantry.Domain.Modules.Tenancy.Managers;

namespace CreatorPantry.Domain.Modules.Tenancy.Data;

public interface IWorkspaceRepository
{
    /// <summary>
    /// Looks up a workspace by slug and the given user's membership in it, if either exists. Returns raw
    /// truth only: disclosure policy (collapsing unknown/inaccessible into one outcome) is Business's job.
    /// </summary>
    Task<WorkspaceMembershipLookup> FindBySlugAsync(string slug, string userId, CancellationToken cancellationToken);

    Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken);

    /// <summary>Atomically inserts a new <c>Workspace</c> and its creator's <c>Owner</c> membership.</summary>
    Task<CreatedWorkspace> CreateWithOwnerAsync(
        string name, string slug, string ownerUserId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<WorkspaceRecord?> FindByIdAsync(Guid workspaceId, CancellationToken cancellationToken);

    Task<WorkspaceRecord> RenameAsync(Guid workspaceId, string name, CancellationToken cancellationToken);

    /// <summary>
    /// Every workspace membership the given user holds, joined with each workspace's own fields. Queried by
    /// an explicit <c>UserId</c> predicate — the same pre-resolution-safe pattern as <see cref="FindBySlugAsync"/>,
    /// since this is used by <c>GET /api/v1/me</c> before any single workspace is resolved.
    /// </summary>
    Task<IReadOnlyList<WorkspaceMembershipRow>> FindMembershipsForUserAsync(string userId, CancellationToken cancellationToken);

    /// <summary>
    /// The display names of the given memberships of the resolved workspace, for naming the person behind a
    /// recorded action.
    /// </summary>
    /// <returns>
    /// A name per membership that exists <em>in this workspace</em>. A membership id that belongs to another
    /// workspace, or to nobody, is simply absent — it is not an error, and the caller decides what an unnamed
    /// actor reads as.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Scoped by the query filter, not by a predicate.</strong> <c>WorkspaceMembership</c> is
    /// workspace-owned, so a membership belonging to another workspace is not visible to be matched — which
    /// is what makes it safe to accept ids that came from another module's rows. A recipe version records the
    /// membership that wrote it and nothing stops a stale or foreign id being stored; this read answers
    /// "which of these are ours" as a side effect of asking at all.
    /// </para>
    /// <para>
    /// <strong>Joined to <c>ApplicationUser</c>, which is Auth's entity.</strong> Permitted for the reason
    /// any cross-module entity reference is: a real foreign key already crosses here —
    /// <c>WorkspaceMembershipConfiguration</c> declares it — and the display name is the one thing a
    /// membership cannot say about itself. Tenancy owns "who belongs to this workspace", so answering "and
    /// what are they called" is this module's question rather than a caller's to assemble.
    /// </para>
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, string>> FindMemberDisplayNamesAsync(
        IReadOnlyCollection<Guid> membershipIds,
        CancellationToken cancellationToken);
}
