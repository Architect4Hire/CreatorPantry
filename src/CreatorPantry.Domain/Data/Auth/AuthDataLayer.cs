using CreatorPantry.Domain.Data.Repositories;
using CreatorPantry.Domain.Gateways.AccountMessages;
using CreatorPantry.Domain.Models.DomainModels.Auth;

namespace CreatorPantry.Domain.Data.Auth;

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
