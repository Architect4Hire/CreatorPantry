using CreatorPantry.Domain.Modules.Auth.Business;
using CreatorPantry.Domain.Modules.Auth.Data;
using CreatorPantry.Domain.Modules.Tenancy.Data;
using CreatorPantry.Domain.Modules.Measurement.Data;
using CreatorPantry.Domain.Modules.Vocabulary.Data;
using CreatorPantry.Domain.Modules.Ingredients.Data;
using CreatorPantry.Domain.Modules.Auth.Facade;
using CreatorPantry.Domain.Modules.Auth.Managers;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;

namespace CreatorPantry.Tests.Auth;

internal sealed class FixedClock : IClock
{
    public DateTimeOffset UtcNow => SqliteAuthServices.Now;
}

internal sealed class FakeUserRepository : IUserRepository
{
    public UserCreationResult CreateResult { get; set; } = UserCreationResult.Created("u1");

    public string ResetToken { get; set; } = "encoded-token";

    public List<string> Calls { get; } = [];

    public List<(NewUser User, string Password, CancellationToken Token)> Creates { get; } = [];

    public Task<UserCreationResult> CreateAsync(NewUser user, string password, CancellationToken cancellationToken)
    {
        Creates.Add((user, password, cancellationToken));
        return Task.FromResult(CreateResult);
    }

    public Task<UserAccount?> FindByEmailAsync(string email, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<UserAccount?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<string> GeneratePasswordResetTokenAsync(string userId, CancellationToken cancellationToken)
    {
        Calls.Add($"token:{userId}");
        return Task.FromResult(ResetToken);
    }

    public Task<PasswordUpdateResult> ResetPasswordAsync(string userId, string encodedToken, string newPassword, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<string> GenerateEmailConfirmationTokenAsync(string userId, CancellationToken cancellationToken)
    {
        Calls.Add($"confirmation-token:{userId}");
        return Task.FromResult(ResetToken);
    }

    public EmailConfirmationResult ConfirmResult { get; set; } = EmailConfirmationResult.Of(EmailConfirmationStatus.Succeeded);

    public List<(string UserId, string EncodedToken)> ConfirmCalls { get; } = [];

    public Task<EmailConfirmationResult> ConfirmEmailAsync(string userId, string encodedToken, CancellationToken cancellationToken)
    {
        ConfirmCalls.Add((userId, encodedToken));
        return Task.FromResult(ConfirmResult);
    }

    public Task<PasswordUpdateResult> ChangePasswordAsync(string userId, string currentPassword, string newPassword, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<CredentialCheckResult> CheckCredentialsAsync(string email, string password, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<SessionUser?> GetSessionUserAsync(string userId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class FakeAuthDataLayer : IAuthDataLayer
{
    public UserCreationResult CreateResult { get; set; } = UserCreationResult.Created("u1");

    public UserAccount? Account { get; set; }

    public PasswordUpdateResult UpdateResult { get; set; } = PasswordUpdateResult.Of(PasswordUpdateStatus.Succeeded);

    public List<(NewUser User, string Password)> Registrations { get; } = [];

    public List<(UserAccount Account, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt)> IssuedResets { get; } = [];

    public List<(UserAccount Account, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt)> IssuedConfirmations { get; } = [];

    public List<UserAccount> ChangedNotices { get; } = [];

    public int ResetCalls { get; private set; }

    public EmailConfirmationResult ConfirmationResult { get; set; } = EmailConfirmationResult.Of(EmailConfirmationStatus.Succeeded);

    public int ConfirmEmailCalls { get; private set; }

    public Task<UserCreationResult> RegisterUserAsync(NewUser user, string password, CancellationToken cancellationToken)
    {
        Registrations.Add((user, password));
        return Task.FromResult(CreateResult);
    }

    public Task<UserAccount?> FindAccountByEmailAsync(string email, CancellationToken cancellationToken) =>
        Task.FromResult(Account);

    public Task<UserAccount?> FindAccountByIdAsync(string userId, CancellationToken cancellationToken) =>
        Task.FromResult(Account);

    public Task IssuePasswordResetAsync(UserAccount account, DateTimeOffset issuedAt, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        IssuedResets.Add((account, issuedAt, expiresAt));
        return Task.CompletedTask;
    }

    public Task<PasswordUpdateResult> ResetPasswordAsync(UserAccount account, string encodedToken, string newPassword, CancellationToken cancellationToken)
    {
        ResetCalls++;
        return Task.FromResult(UpdateResult);
    }

    public Task IssueEmailConfirmationAsync(UserAccount account, DateTimeOffset issuedAt, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        IssuedConfirmations.Add((account, issuedAt, expiresAt));
        return Task.CompletedTask;
    }

    public Task<EmailConfirmationResult> ConfirmEmailAsync(UserAccount account, string encodedToken, CancellationToken cancellationToken)
    {
        ConfirmEmailCalls++;
        return Task.FromResult(ConfirmationResult);
    }

    public Task<PasswordUpdateResult> ChangePasswordAsync(string userId, string currentPassword, string newPassword, CancellationToken cancellationToken) =>
        Task.FromResult(UpdateResult);

    public Task SendPasswordChangedNoticeAsync(UserAccount account, DateTimeOffset sentAt, CancellationToken cancellationToken)
    {
        ChangedNotices.Add(account);
        return Task.CompletedTask;
    }

    public CredentialCheckResult CredentialResult { get; set; } = CredentialCheckResult.Of(CredentialCheckStatus.Invalid);

    public SessionUser? SessionUser { get; set; }

    public Task<CredentialCheckResult> CheckCredentialsAsync(string email, string password, CancellationToken cancellationToken) =>
        Task.FromResult(CredentialResult);

    public Task<SessionUser?> GetSessionUserAsync(string userId, CancellationToken cancellationToken) =>
        Task.FromResult(SessionUser);
}

internal sealed class FakeAuthBusiness : IAuthBusiness
{
    public int Calls { get; private set; }

    public Task<OperationResult<RegistrationServiceModel>> RegisterAsync(RegisterUserViewModel model, CancellationToken cancellationToken) =>
        Count(RegistrationServiceModel.PendingConfirmation);

    public Task<OperationResult<PasswordServiceModel>> RequestPasswordResetAsync(RequestPasswordResetViewModel model, CancellationToken cancellationToken) =>
        Count(PasswordServiceModel.ResetRequested);

    public Task<OperationResult<PasswordServiceModel>> CompletePasswordResetAsync(CompletePasswordResetViewModel model, CancellationToken cancellationToken) =>
        Count(PasswordServiceModel.ResetCompleted);

    public Task<OperationResult<EmailConfirmationServiceModel>> ConfirmEmailAsync(ConfirmEmailViewModel model, CancellationToken cancellationToken) =>
        Count(EmailConfirmationServiceModel.Confirmed);

    public Task<OperationResult<PasswordServiceModel>> ChangePasswordAsync(string userId, ChangePasswordViewModel model, CancellationToken cancellationToken) =>
        Count(PasswordServiceModel.Changed);

    public Task<OperationResult<SessionUserServiceModel>> VerifyCredentialsAsync(VerifyCredentialsViewModel model, CancellationToken cancellationToken) =>
        Count(new SessionUserServiceModel("u1", "Sam", [], "stamp"));

    public Task<OperationResult<SessionUserServiceModel>> ValidateSessionAsync(ValidateSessionViewModel model, CancellationToken cancellationToken) =>
        Count(new SessionUserServiceModel("u1", "Sam", [], "stamp"));

    private Task<OperationResult<T>> Count<T>(T value)
    {
        Calls++;
        return Task.FromResult(OperationResult<T>.Success(value));
    }
}

/// <summary>Returns a configured result (or throws) for every operation.</summary>
internal sealed class FakeAuthFacade(object? result = null, Exception? error = null) : IAuthFacade
{
    public int Calls { get; private set; }

    public Task<OperationResult<RegistrationServiceModel>> RegisterAsync(RegisterUserViewModel model, CancellationToken cancellationToken) =>
        Respond<RegistrationServiceModel>();

    public Task<OperationResult<PasswordServiceModel>> RequestPasswordResetAsync(RequestPasswordResetViewModel model, CancellationToken cancellationToken) =>
        Respond<PasswordServiceModel>();

    public Task<OperationResult<PasswordServiceModel>> CompletePasswordResetAsync(CompletePasswordResetViewModel model, CancellationToken cancellationToken) =>
        Respond<PasswordServiceModel>();

    public Task<OperationResult<EmailConfirmationServiceModel>> ConfirmEmailAsync(ConfirmEmailViewModel model, CancellationToken cancellationToken) =>
        Respond<EmailConfirmationServiceModel>();

    public Task<OperationResult<PasswordServiceModel>> ChangePasswordAsync(string userId, ChangePasswordViewModel model, CancellationToken cancellationToken) =>
        Respond<PasswordServiceModel>();

    public Task<OperationResult<SessionUserServiceModel>> VerifyCredentialsAsync(VerifyCredentialsViewModel model, CancellationToken cancellationToken) =>
        Respond<SessionUserServiceModel>();

    public Task<OperationResult<SessionUserServiceModel>> ValidateSessionAsync(ValidateSessionViewModel model, CancellationToken cancellationToken) =>
        Respond<SessionUserServiceModel>();

    private Task<OperationResult<T>> Respond<T>()
    {
        Calls++;
        return error is null
            ? Task.FromResult((OperationResult<T>)result!)
            : Task.FromException<OperationResult<T>>(error);
    }
}
