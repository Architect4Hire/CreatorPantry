using CreatorPantry.Domain.Modules.Auth.Managers;
using CreatorPantry.Domain.Modules.Auth.Business;
using CreatorPantry.Domain.Managers.Results;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Auth.Facade;

internal sealed class AuthFacade(
    IValidator<RegisterUserViewModel> registerValidator,
    IValidator<RequestPasswordResetViewModel> requestResetValidator,
    IValidator<CompletePasswordResetViewModel> completeResetValidator,
    IValidator<ChangePasswordViewModel> changePasswordValidator,
    IValidator<ConfirmEmailViewModel> confirmEmailValidator,
    IValidator<VerifyCredentialsViewModel> verifyCredentialsValidator,
    IValidator<ValidateSessionViewModel> validateSessionValidator,
    IAuthBusiness business) : IAuthFacade
{
    public async Task<OperationResult<SessionUserServiceModel>> VerifyCredentialsAsync(
        VerifyCredentialsViewModel model, CancellationToken cancellationToken) =>
        await ValidateAsync<VerifyCredentialsViewModel, SessionUserServiceModel>(
            verifyCredentialsValidator, model, AuthErrorCodes.SignInInvalidRequest, cancellationToken)
        ?? await business.VerifyCredentialsAsync(model, cancellationToken);

    public async Task<OperationResult<SessionUserServiceModel>> ValidateSessionAsync(
        ValidateSessionViewModel model, CancellationToken cancellationToken) =>
        await ValidateAsync<ValidateSessionViewModel, SessionUserServiceModel>(
            validateSessionValidator, model, AuthErrorCodes.SessionInvalid, cancellationToken)
        ?? await business.ValidateSessionAsync(model, cancellationToken);

    public async Task<OperationResult<RegistrationServiceModel>> RegisterAsync(
        RegisterUserViewModel model, CancellationToken cancellationToken) =>
        await ValidateAsync<RegisterUserViewModel, RegistrationServiceModel>(
            registerValidator, model, AuthErrorCodes.RegistrationInvalid, cancellationToken)
        ?? await business.RegisterAsync(model, cancellationToken);

    public async Task<OperationResult<PasswordServiceModel>> RequestPasswordResetAsync(
        RequestPasswordResetViewModel model, CancellationToken cancellationToken) =>
        await ValidateAsync<RequestPasswordResetViewModel, PasswordServiceModel>(
            requestResetValidator, model, AuthErrorCodes.PasswordResetInvalid, cancellationToken)
        ?? await business.RequestPasswordResetAsync(model, cancellationToken);

    public async Task<OperationResult<PasswordServiceModel>> CompletePasswordResetAsync(
        CompletePasswordResetViewModel model, CancellationToken cancellationToken) =>
        await ValidateAsync<CompletePasswordResetViewModel, PasswordServiceModel>(
            completeResetValidator, model, AuthErrorCodes.PasswordResetInvalid, cancellationToken)
        ?? await business.CompletePasswordResetAsync(model, cancellationToken);

    public async Task<OperationResult<EmailConfirmationServiceModel>> ConfirmEmailAsync(
        ConfirmEmailViewModel model, CancellationToken cancellationToken) =>
        await ValidateAsync<ConfirmEmailViewModel, EmailConfirmationServiceModel>(
            confirmEmailValidator, model, AuthErrorCodes.EmailConfirmationInvalid, cancellationToken)
        ?? await business.ConfirmEmailAsync(model, cancellationToken);

    public async Task<OperationResult<PasswordServiceModel>> ChangePasswordAsync(
        string userId, ChangePasswordViewModel model, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return await ValidateAsync<ChangePasswordViewModel, PasswordServiceModel>(
                changePasswordValidator, model, AuthErrorCodes.PasswordChangeInvalid, cancellationToken)
            ?? await business.ChangePasswordAsync(userId, model, cancellationToken);
    }

    /// <summary>Returns a failure when the input is invalid; null when it may proceed.</summary>
    private static async Task<OperationResult<TResult>?> ValidateAsync<TModel, TResult>(
        IValidator<TModel> validator, TModel model, string code, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(model, cancellationToken);

        return validation.IsValid
            ? null
            : OperationResult<TResult>.Failure(OperationError.Validation(
                code, "The request is invalid.", validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
    }
}
