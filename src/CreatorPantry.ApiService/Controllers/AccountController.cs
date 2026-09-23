using Asp.Versioning;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Modules.Auth.Facade;
using CreatorPantry.Domain.Modules.Auth.Managers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>The signed-in user's own account. Requires a user session (default policy).</summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/account")]
public sealed class AccountController(IAuthFacade authFacade) : ControllerBase
{
    /// <summary>
    /// Changes the signed-in user's password. Success rotates the security stamp, which ends every session for
    /// the user (including this one) at its next revalidation, so the user signs in again with the new password.
    /// </summary>
    [HttpPost("password")]
    [ProducesResponseType<PasswordServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model, CancellationToken cancellationToken)
    {
        // The user id comes only from the gateway-signed token, never from the request.
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;

        var result = await authFacade.ChangePasswordAsync(userId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}
