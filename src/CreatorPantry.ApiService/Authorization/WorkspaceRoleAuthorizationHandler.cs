using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Managers.Persistence;
using Microsoft.AspNetCore.Authorization;

namespace CreatorPantry.ApiService.Authorization;

/// <summary>
/// Satisfies a <see cref="WorkspaceRoleRequirement"/> from <see cref="IWorkspaceContext"/> — the workspace
/// resolved for this scope, and the caller's already-verified active membership role within it (tenancy.md;
/// resolution never populates this from an inactive or missing membership). Fails closed: an unresolved
/// context (no workspace on the route, or resolution failed/never ran) and a resolved role below the
/// requirement both simply never call <see cref="AuthorizationHandlerContext.Succeed"/>, which
/// ASP.NET Core Authorization treats as denied without any explicit <c>Fail()</c> call here.
/// </summary>
internal sealed class WorkspaceRoleAuthorizationHandler(IWorkspaceContext workspaceContext)
    : AuthorizationHandler<WorkspaceRoleRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, WorkspaceRoleRequirement requirement)
    {
        if (workspaceContext.IsResolved && workspaceContext.Role >= requirement.MinimumRole)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
