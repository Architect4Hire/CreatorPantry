using CreatorPantry.Domain.Modules.Auth.Managers;
using CreatorPantry.Domain.Modules.Auth.Gateways;

namespace CreatorPantry.Domain.Modules.Auth.Data;

internal sealed class AuthDataLayer(IUserRepository users, IAccountMessageSender messages) : IAuthDataLayer
{
    public Task<UserCreationResult> RegisterUserAsync(NewUser user, string password, CancellationToken cancellationToken) =>
        users.CreateAsync(user, password, cancellationToken);

    public Task<UserAccount?> FindAccountByEmailAsync(string email, CancellationToken cancellationToken) =>
        users.FindByEmailAsync(email, cancellationToken);

    public Task<UserAccount?> FindAccountByIdAsync(string userId, CancellationToken cancellationToken) =>
        users.FindByIdAsync(userId, cancellationToken);

    public async Task IssuePasswordResetAsync(
        UserAccount account, DateTimeOffset issuedAt, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var token = await users.GeneratePasswordResetTokenAsync(account.UserId, cancellationToken);

        await messages.SendAsync(
            AccountMessage.PasswordReset(account.UserId, account.Email, token, issuedAt, expiresAt), cancellationToken);
    }

    public Task<PasswordUpdateResult> ResetPasswordAsync(
        UserAccount account, string encodedToken, string newPassword, CancellationToken cancellationToken) =>
        users.ResetPasswordAsync(account.UserId, encodedToken, newPassword, cancellationToken);

    public async Task IssueEmailConfirmationAsync(
        UserAccount account, DateTimeOffset issuedAt, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var token = await users.GenerateEmailConfirmationTokenAsync(account.UserId, cancellationToken);

        await messages.SendAsync(
            AccountMessage.EmailConfirmation(account.UserId, account.Email, token, issuedAt, expiresAt), cancellationToken);
    }

    public Task<EmailConfirmationResult> ConfirmEmailAsync(UserAccount account, string encodedToken, CancellationToken cancellationToken) =>
        users.ConfirmEmailAsync(account.UserId, encodedToken, cancellationToken);

    public Task<PasswordUpdateResult> ChangePasswordAsync(
        string userId, string currentPassword, string newPassword, CancellationToken cancellationToken) =>
        users.ChangePasswordAsync(userId, currentPassword, newPassword, cancellationToken);

    public Task SendPasswordChangedNoticeAsync(UserAccount account, DateTimeOffset sentAt, CancellationToken cancellationToken) =>
        messages.SendAsync(AccountMessage.PasswordChanged(account.UserId, account.Email, sentAt), cancellationToken);

    public Task<CredentialCheckResult> CheckCredentialsAsync(string email, string password, CancellationToken cancellationToken) =>
        users.CheckCredentialsAsync(email, password, cancellationToken);

    public Task<SessionUser?> GetSessionUserAsync(string userId, CancellationToken cancellationToken) =>
        users.GetSessionUserAsync(userId, cancellationToken);
}
