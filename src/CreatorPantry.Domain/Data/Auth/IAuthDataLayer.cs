using CreatorPantry.Domain.Models.DomainModels.Auth;

namespace CreatorPantry.Domain.Data.Auth;

public interface IAuthDataLayer
{
    /// <summary>Persists a new user. A single Identity write, so no additional transaction is opened.</summary>
    Task<UserCreationResult> RegisterUserAsync(NewUser user, string password, CancellationToken cancellationToken);

    Task<UserAccount?> FindAccountByEmailAsync(string email, CancellationToken cancellationToken);

    Task<UserAccount?> FindAccountByIdAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Generates a reset token and hands it to the account message sender.</summary>
    Task IssuePasswordResetAsync(UserAccount account, DateTimeOffset issuedAt, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    Task<PasswordUpdateResult> ResetPasswordAsync(UserAccount account, string encodedToken, string newPassword, CancellationToken cancellationToken);

    Task<PasswordUpdateResult> ChangePasswordAsync(string userId, string currentPassword, string newPassword, CancellationToken cancellationToken);

    Task SendPasswordChangedNoticeAsync(UserAccount account, DateTimeOffset sentAt, CancellationToken cancellationToken);

    Task<CredentialCheckResult> CheckCredentialsAsync(string email, string password, CancellationToken cancellationToken);

    Task<SessionUser?> GetSessionUserAsync(string userId, CancellationToken cancellationToken);
}
