using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Facade.Auth;
using CreatorPantry.Domain.Models.ServiceModels.Auth;
using CreatorPantry.Domain.Models.ViewModels.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>
/// Gateway-only session support (baseline B-13). Callable only with the gateway's service token; the gateway
/// also refuses to proxy <c>/api/*/internal/**</c> from browsers. Not published in OpenAPI.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/internal/sessions")]
[Authorize(Policy = AuthorizationPolicies.GatewayService)]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class InternalSessionsController(IAuthFacade authFacade) : ControllerBase
{
    /// <summary>Verifies sign-in credentials submitted to the gateway.</summary>
    [HttpPost]
    public async Task<IActionResult> Verify(VerifyCredentialsViewModel model, CancellationToken cancellationToken)
    {
        var result = await authFacade.VerifyCredentialsAsync(model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Confirms a session's user still exists with the same security stamp.</summary>
    [HttpPost("validate")]
    public async Task<IActionResult> Validate(ValidateSessionViewModel model, CancellationToken cancellationToken)
    {
        var result = await authFacade.ValidateSessionAsync(model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}
