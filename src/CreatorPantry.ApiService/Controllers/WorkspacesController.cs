using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces")]
public sealed class WorkspacesController(IWorkspaceFacade workspaceFacade) : ControllerBase
{
    /// <summary>Creates a workspace. The caller becomes its Owner atomically with the workspace itself.</summary>
    [HttpPost]
    [ProducesResponseType<WorkspaceServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Create(CreateWorkspaceViewModel model, CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var result = await workspaceFacade.CreateAsync(userId, model, cancellationToken);
        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        return Created($"/api/v1/workspaces/{result.Value!.Slug}", result.Value);
    }

    /// <summary>The workspace resolved from the route, plus the caller's own membership in it.</summary>
    [HttpGet("{workspaceSlug}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<WorkspaceServiceModel>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string workspaceSlug, CancellationToken cancellationToken) =>
        Ok(await workspaceFacade.GetCurrentAsync(cancellationToken));

    /// <summary>Renames the workspace resolved from the route. The address (slug) does not change.</summary>
    [HttpPatch("{workspaceSlug}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceOwner)]
    [ProducesResponseType<WorkspaceServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> Update(string workspaceSlug, UpdateWorkspaceViewModel model, CancellationToken cancellationToken)
    {
        var result = await workspaceFacade.RenameCurrentAsync(model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}
