using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Tenancy.Managers;

/// <summary>
/// A workspace and the caller's confirmed active membership in it. Carries exactly what
/// <c>IWorkspaceContextResolver.Resolve</c> needs.
/// </summary>
/// <param name="AccountId">
/// The Identity account behind <paramref name="MembershipId"/>, taken from the same verified
/// <c>WorkspaceMembership</c> row. Surfaced because per-account AI usage accounting (USAGE-001) cannot be
/// derived from a workspace-scoped membership id — and surfaced <em>here</em> because this row is already
/// read to resolve the workspace, so carrying it costs nothing.
/// </param>
public sealed record ResolvedWorkspaceServiceModel(
    Guid WorkspaceId, string WorkspaceSlug, Guid MembershipId, WorkspaceRole Role, string AccountId);
