using Asp.Versioning;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Facade.Auth;
using CreatorPantry.Domain.Models.ServiceModels.Auth;
using CreatorPantry.Domain.Models.ViewModels.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/auth")]
public sealed class AuthController(IAuthFacade authFacade) : ControllerBase
{
    /// <summary>
    /// Registers an account. The 202 response is identical whether or not the email is already registered.
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType<RegistrationServiceModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> Register(RegisterUserViewModel model, CancellationToken cancellationToken)
    {
        var result = await authFacade.RegisterAsync(model, cancellationToken);

        return result.Succeeded
            ? Accepted(result.Value)
            : this.ProblemFor(result.Error!);
    }

    /// <summary>
    /// Starts a password reset. The 202 response is identical whether or not the email belongs to an account.
    /// </summary>
    [HttpPost("password-reset")]
    [AllowAnonymous]
    [ProducesResponseType<PasswordServiceModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> RequestPasswordReset(RequestPasswordResetViewModel model, CancellationToken cancellationToken)
    {
        var result = await authFacade.RequestPasswordResetAsync(model, cancellationToken);

        return result.Succeeded
            ? Accepted(result.Value)
            : this.ProblemFor(result.Error!);
    }

    /// <summary>
    /// Completes a password reset. Unknown emails and invalid, expired, or used tokens share one error.
    /// </summary>
    [HttpPost("password-reset/complete")]
    [AllowAnonymous]
    [ProducesResponseType<PasswordServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> CompletePasswordReset(CompletePasswordResetViewModel model, CancellationToken cancellationToken)
    {
        var result = await authFacade.CompletePasswordResetAsync(model, cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : this.ProblemFor(result.Error!);
    }
}
