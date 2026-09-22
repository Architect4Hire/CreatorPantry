using CreatorPantry.Domain.Tenancy;

namespace CreatorPantry.Domain.Models.ServiceModels.Tenancy;

/// <summary>One of the signed-in user's own workspace memberships, for <c>GET /api/v1/me</c>.</summary>
public sealed record MyWorkspaceMembershipServiceModel(
    Guid WorkspaceId,
    string WorkspaceSlug,
    string WorkspaceName,
    Guid MembershipId,
    WorkspaceRole Role,
    WorkspaceMembershipStatus Status);
