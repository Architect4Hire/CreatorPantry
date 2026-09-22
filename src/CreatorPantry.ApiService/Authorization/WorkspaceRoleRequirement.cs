using CreatorPantry.Domain.Tenancy;
using Microsoft.AspNetCore.Authorization;

namespace CreatorPantry.ApiService.Authorization;

/// <summary>
/// "The caller's role in the resolved workspace is at least <see cref="MinimumRole"/>." The one requirement
/// type behind every <c>Workspace*</c> policy in <see cref="AuthorizationPolicies"/> — they differ only in
/// which <see cref="WorkspaceRole"/> they pass, so role implication is encoded once, in
/// <see cref="WorkspaceRoleAuthorizationHandler"/>, not repeated per policy or per endpoint.
/// </summary>
public sealed record WorkspaceRoleRequirement(WorkspaceRole MinimumRole) : IAuthorizationRequirement;
