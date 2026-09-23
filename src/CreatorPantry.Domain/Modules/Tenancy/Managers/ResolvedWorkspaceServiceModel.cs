using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Tenancy.Managers;

/// <summary>
/// A workspace and the caller's confirmed active membership in it. Carries exactly what
/// <c>IWorkspaceContextResolver.Resolve</c> needs.
/// </summary>
public sealed record ResolvedWorkspaceServiceModel(Guid WorkspaceId, string WorkspaceSlug, Guid MembershipId, WorkspaceRole Role);
