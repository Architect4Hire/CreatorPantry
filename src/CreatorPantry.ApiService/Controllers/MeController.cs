using Asp.Versioning;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>The signed-in user's own cross-workspace view. Requires a user session (default policy).</summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/me")]
public sealed class MeController(IWorkspaceFacade workspaceFacade) : ControllerBase
{
    /// <summary>Every workspace membership the signed-in user holds, active or not.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<MyWorkspaceMembershipServiceModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;

        return Ok(await workspaceFacade.GetMyMembershipsAsync(userId, cancellationToken));
    }
}
