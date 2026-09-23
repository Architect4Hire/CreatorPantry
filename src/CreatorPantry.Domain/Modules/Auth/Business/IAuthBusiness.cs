using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Auth.Managers;

namespace CreatorPantry.Domain.Modules.Auth.Business;

/// <summary>Account rules. Inputs have already passed shape validation in the facade.</summary>
public interface IAuthBusiness
{
    Task<OperationResult<RegistrationServiceModel>> RegisterAsync(RegisterUserViewModel model, CancellationToken cancellationToken);

    Task<OperationResult<PasswordServiceModel>> RequestPasswordResetAsync(RequestPasswordResetViewModel model, CancellationToken cancellationToken);

    Task<OperationResult<PasswordServiceModel>> CompletePasswordResetAsync(CompletePasswordResetViewModel model, CancellationToken cancellationToken);

    Task<OperationResult<EmailConfirmationServiceModel>> ConfirmEmailAsync(ConfirmEmailViewModel model, CancellationToken cancellationToken);

    Task<OperationResult<PasswordServiceModel>> ChangePasswordAsync(string userId, ChangePasswordViewModel model, CancellationToken cancellationToken);

    Task<OperationResult<SessionUserServiceModel>> VerifyCredentialsAsync(VerifyCredentialsViewModel model, CancellationToken cancellationToken);

    Task<OperationResult<SessionUserServiceModel>> ValidateSessionAsync(ValidateSessionViewModel model, CancellationToken cancellationToken);
}
