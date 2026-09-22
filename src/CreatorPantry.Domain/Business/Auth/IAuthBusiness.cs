using CreatorPantry.Domain.Models.Results;
using CreatorPantry.Domain.Models.ServiceModels.Auth;
using CreatorPantry.Domain.Models.ViewModels.Auth;

namespace CreatorPantry.Domain.Business.Auth;

/// <summary>Account rules. Inputs have already passed shape validation in the facade.</summary>
public interface IAuthBusiness
{
    Task<OperationResult<RegistrationServiceModel>> RegisterAsync(RegisterUserViewModel model, CancellationToken cancellationToken);

    Task<OperationResult<PasswordServiceModel>> RequestPasswordResetAsync(RequestPasswordResetViewModel model, CancellationToken cancellationToken);

    Task<OperationResult<PasswordServiceModel>> CompletePasswordResetAsync(CompletePasswordResetViewModel model, CancellationToken cancellationToken);

    Task<OperationResult<PasswordServiceModel>> ChangePasswordAsync(string userId, ChangePasswordViewModel model, CancellationToken cancellationToken);

    Task<OperationResult<SessionUserServiceModel>> VerifyCredentialsAsync(VerifyCredentialsViewModel model, CancellationToken cancellationToken);

    Task<OperationResult<SessionUserServiceModel>> ValidateSessionAsync(ValidateSessionViewModel model, CancellationToken cancellationToken);
}
