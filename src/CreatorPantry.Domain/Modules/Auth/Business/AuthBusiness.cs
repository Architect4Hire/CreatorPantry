using CreatorPantry.Domain.Modules.Auth.Managers;
using CreatorPantry.Domain.Modules.Auth.Data;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Auth.Business;

internal sealed class AuthBusiness(IAuthDataLayer dataLayer, IClock clock, ILogger<AuthBusiness> logger) : IAuthBusiness
{
    private const string InvalidRequestMessage = "The request is invalid.";
    private const string InvalidTokenMessage = "This reset link is invalid or has expired.";
    private const string InvalidConfirmationTokenMessage = "This confirmation link is invalid or has expired.";

    public async Task<OperationResult<RegistrationServiceModel>> RegisterAsync(
        RegisterUserViewModel model, CancellationToken cancellationToken)
    {
        var email = model.Email.Trim();
        var user = new NewUser(email, model.DisplayName.Trim(), clock.UtcNow);

        var result = await dataLayer.RegisterUserAsync(user, model.Password, cancellationToken);

        switch (result.Status)
        {
            case UserCreationStatus.Created:
                // Only the new account receives a confirmation link; a duplicate would send a token for
                // someone else's account. The public response is identical either way.
                var issuedAt = clock.UtcNow;
                await dataLayer.IssueEmailConfirmationAsync(new UserAccount(result.UserId!, email, EmailConfirmed: false),
                    issuedAt, issuedAt + AccountPolicy.PasswordResetTokenLifetime, cancellationToken);
                goto case UserCreationStatus.Duplicate;

            // Identical responses: registration never reveals whether an email already has an account.
            case UserCreationStatus.Duplicate:
                return OperationResult<RegistrationServiceModel>.Success(RegistrationServiceModel.PendingConfirmation);

            case UserCreationStatus.InvalidPassword:
                return Invalid<RegistrationServiceModel>(AuthErrorCodes.RegistrationInvalid, nameof(RegisterUserViewModel.Password), result.Errors);
            case UserCreationStatus.InvalidEmail:
                return Invalid<RegistrationServiceModel>(AuthErrorCodes.RegistrationInvalid, nameof(RegisterUserViewModel.Email), result.Errors);

            default:
                throw new InvalidOperationException($"Unhandled user creation status {result.Status}.");
        }
    }

    public async Task<OperationResult<PasswordServiceModel>> RequestPasswordResetAsync(
        RequestPasswordResetViewModel model, CancellationToken cancellationToken)
    {
        var account = await dataLayer.FindAccountByEmailAsync(model.Email.Trim(), cancellationToken);

        // Only confirmed addresses receive a reset link; every caller gets the same response.
        if (account is { EmailConfirmed: true })
        {
            var issuedAt = clock.UtcNow;
            await dataLayer.IssuePasswordResetAsync(
                account, issuedAt, issuedAt + AccountPolicy.PasswordResetTokenLifetime, cancellationToken);
        }
        else
        {
            logger.LogInformation("Password reset requested for an unknown or unconfirmed address; nothing sent.");
        }

        return OperationResult<PasswordServiceModel>.Success(PasswordServiceModel.ResetRequested);
    }

    public async Task<OperationResult<PasswordServiceModel>> CompletePasswordResetAsync(
        CompletePasswordResetViewModel model, CancellationToken cancellationToken)
    {
        var account = await dataLayer.FindAccountByEmailAsync(model.Email.Trim(), cancellationToken);
        if (account is null)
        {
            return InvalidToken();
        }

        var result = await dataLayer.ResetPasswordAsync(account, model.Token, model.NewPassword, cancellationToken);

        switch (result.Status)
        {
            case PasswordUpdateStatus.Succeeded:
                await dataLayer.SendPasswordChangedNoticeAsync(account, clock.UtcNow, cancellationToken);
                return OperationResult<PasswordServiceModel>.Success(PasswordServiceModel.ResetCompleted);

            case PasswordUpdateStatus.InvalidPassword:
                return Invalid<PasswordServiceModel>(
                    AuthErrorCodes.PasswordResetInvalid, nameof(CompletePasswordResetViewModel.NewPassword), result.Errors);

            case PasswordUpdateStatus.InvalidToken:
            case PasswordUpdateStatus.NotFound:
                return InvalidToken();

            default:
                throw new InvalidOperationException($"Unhandled password reset status {result.Status}.");
        }
    }

    public async Task<OperationResult<PasswordServiceModel>> ChangePasswordAsync(
        string userId, ChangePasswordViewModel model, CancellationToken cancellationToken)
    {
        var result = await dataLayer.ChangePasswordAsync(userId, model.CurrentPassword, model.NewPassword, cancellationToken);

        switch (result.Status)
        {
            case PasswordUpdateStatus.Succeeded:
                var account = await dataLayer.FindAccountByIdAsync(userId, cancellationToken);
                if (account is not null)
                {
                    await dataLayer.SendPasswordChangedNoticeAsync(account, clock.UtcNow, cancellationToken);
                }

                return OperationResult<PasswordServiceModel>.Success(PasswordServiceModel.Changed);

            case PasswordUpdateStatus.IncorrectCurrentPassword:
                return Invalid<PasswordServiceModel>(AuthErrorCodes.PasswordChangeInvalid,
                    nameof(ChangePasswordViewModel.CurrentPassword), ["Your current password is incorrect."]);

            case PasswordUpdateStatus.InvalidPassword:
                return Invalid<PasswordServiceModel>(
                    AuthErrorCodes.PasswordChangeInvalid, nameof(ChangePasswordViewModel.NewPassword), result.Errors);

            case PasswordUpdateStatus.NotFound:
                return OperationResult<PasswordServiceModel>.Failure(
                    new OperationError(AuthErrorCodes.AccountNotFound, "The account no longer exists.",
                        new Dictionary<string, string[]>()));

            default:
                throw new InvalidOperationException($"Unhandled password change status {result.Status}.");
        }
    }

    public async Task<OperationResult<EmailConfirmationServiceModel>> ConfirmEmailAsync(
        ConfirmEmailViewModel model, CancellationToken cancellationToken)
    {
        var account = await dataLayer.FindAccountByEmailAsync(model.Email.Trim(), cancellationToken);
        if (account is null)
        {
            return InvalidConfirmationToken();
        }

        var result = await dataLayer.ConfirmEmailAsync(account, model.Token, cancellationToken);

        return result.Status switch
        {
            EmailConfirmationStatus.Succeeded =>
                OperationResult<EmailConfirmationServiceModel>.Success(EmailConfirmationServiceModel.Confirmed),

            EmailConfirmationStatus.InvalidToken or EmailConfirmationStatus.NotFound => InvalidConfirmationToken(),

            _ => throw new InvalidOperationException($"Unhandled email confirmation status {result.Status}."),
        };
    }

    public async Task<OperationResult<SessionUserServiceModel>> VerifyCredentialsAsync(
        VerifyCredentialsViewModel model, CancellationToken cancellationToken)
    {
        var result = await dataLayer.CheckCredentialsAsync(model.Email.Trim(), model.Password, cancellationToken);

        return result.Status switch
        {
            CredentialCheckStatus.Succeeded => OperationResult<SessionUserServiceModel>.Success(ToServiceModel(result.User!)),

            // Unknown email, wrong password, and lockout share one response so none reveals an account.
            CredentialCheckStatus.Invalid or CredentialCheckStatus.LockedOut => Failure<SessionUserServiceModel>(
                AuthErrorCodes.SignInFailed,
                "Sign-in failed. Check your email and password, or wait a few minutes and try again."),

            CredentialCheckStatus.EmailUnconfirmed => Failure<SessionUserServiceModel>(
                AuthErrorCodes.SignInEmailUnconfirmed, "Confirm your email address before signing in."),

            _ => throw new InvalidOperationException($"Unhandled credential check status {result.Status}."),
        };
    }

    public async Task<OperationResult<SessionUserServiceModel>> ValidateSessionAsync(
        ValidateSessionViewModel model, CancellationToken cancellationToken)
    {
        var user = await dataLayer.GetSessionUserAsync(model.UserId, cancellationToken);

        // A changed security stamp means the password was changed or reset; a locked-out or unconfirmed account
        // may not keep a session either.
        return user is { CanSignIn: true } && StampsMatch(user.SecurityStamp, model.SecurityStamp)
            ? OperationResult<SessionUserServiceModel>.Success(ToServiceModel(user))
            : Failure<SessionUserServiceModel>(AuthErrorCodes.SessionInvalid, "The session is no longer valid.");
    }

    private static bool StampsMatch(string current, string presented) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(current), System.Text.Encoding.UTF8.GetBytes(presented));

    private static SessionUserServiceModel ToServiceModel(SessionUser user) =>
        new(user.UserId, user.DisplayName, user.Roles, user.SecurityStamp);

    private static OperationResult<T> Failure<T>(string code, string message) =>
        OperationResult<T>.Failure(new OperationError(code, message, new Dictionary<string, string[]>()));

    private static OperationResult<PasswordServiceModel> InvalidToken() =>
        OperationResult<PasswordServiceModel>.Failure(new OperationError(
            AuthErrorCodes.PasswordResetInvalidToken, InvalidTokenMessage, new Dictionary<string, string[]>()));

    private static OperationResult<EmailConfirmationServiceModel> InvalidConfirmationToken() =>
        OperationResult<EmailConfirmationServiceModel>.Failure(new OperationError(
            AuthErrorCodes.EmailConfirmationInvalidToken, InvalidConfirmationTokenMessage, new Dictionary<string, string[]>()));

    private static OperationResult<T> Invalid<T>(string code, string field, IEnumerable<string> errors) =>
        OperationResult<T>.Failure(OperationError.Validation(
            code, InvalidRequestMessage, errors.Select(error => (field, error))));
}
