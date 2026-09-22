using CreatorPantry.Domain.Tenancy;

namespace CreatorPantry.Domain.Models.ServiceModels.Tenancy;

/// <summary>
/// A workspace and the caller's confirmed active membership in it. Carries exactly what
/// <c>IWorkspaceContextResolver.Resolve</c> needs.
/// </summary>
public sealed record ResolvedWorkspaceServiceModel(Guid WorkspaceId, string WorkspaceSlug, Guid MembershipId, WorkspaceRole Role);
