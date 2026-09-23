using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Auth.Managers;

namespace CreatorPantry.Domain.Modules.Auth.Facade;

/// <summary>Application boundary for authentication, used by controllers and background work.</summary>
public interface IAuthFacade
{
    Task<OperationResult<RegistrationServiceModel>> RegisterAsync(RegisterUserViewModel model, CancellationToken cancellationToken);

    /// <summary>Always succeeds with the same response, whether or not the email belongs to an account.</summary>
    Task<OperationResult<PasswordServiceModel>> RequestPasswordResetAsync(RequestPasswordResetViewModel model, CancellationToken cancellationToken);

    Task<OperationResult<PasswordServiceModel>> CompletePasswordResetAsync(CompletePasswordResetViewModel model, CancellationToken cancellationToken);

    Task<OperationResult<EmailConfirmationServiceModel>> ConfirmEmailAsync(ConfirmEmailViewModel model, CancellationToken cancellationToken);

    /// <param name="userId">The authenticated caller's id. Never taken from request input.</param>
    Task<OperationResult<PasswordServiceModel>> ChangePasswordAsync(string userId, ChangePasswordViewModel model, CancellationToken cancellationToken);

    /// <summary>Gateway-only: verifies sign-in credentials and returns what the session needs.</summary>
    Task<OperationResult<SessionUserServiceModel>> VerifyCredentialsAsync(VerifyCredentialsViewModel model, CancellationToken cancellationToken);

    /// <summary>Gateway-only: confirms a session's user still exists with the same security stamp.</summary>
    Task<OperationResult<SessionUserServiceModel>> ValidateSessionAsync(ValidateSessionViewModel model, CancellationToken cancellationToken);
}
