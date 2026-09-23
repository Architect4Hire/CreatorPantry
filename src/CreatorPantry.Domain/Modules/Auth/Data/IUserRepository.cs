using CreatorPantry.Domain.Modules.Auth.Managers;

namespace CreatorPantry.Domain.Modules.Auth.Data;

/// <summary>User persistence over ASP.NET Core Identity's <c>UserManager</c>.</summary>
public interface IUserRepository
{
    Task<UserCreationResult> CreateAsync(NewUser user, string password, CancellationToken cancellationToken);

    Task<UserAccount?> FindByEmailAsync(string email, CancellationToken cancellationToken);

    Task<UserAccount?> FindByIdAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Returns a URL-safe, single-use reset token for an existing user.</summary>
    Task<string> GeneratePasswordResetTokenAsync(string userId, CancellationToken cancellationToken);

    Task<PasswordUpdateResult> ResetPasswordAsync(string userId, string encodedToken, string newPassword, CancellationToken cancellationToken);

    Task<PasswordUpdateResult> ChangePasswordAsync(string userId, string currentPassword, string newPassword, CancellationToken cancellationToken);

    /// <summary>
    /// Verifies a password with Identity lockout accounting. Unknown, locked, and wrong-password paths all
    /// spend a password-hash computation so their timing does not reveal whether the account exists.
    /// </summary>
    Task<CredentialCheckResult> CheckCredentialsAsync(string email, string password, CancellationToken cancellationToken);

    Task<SessionUser?> GetSessionUserAsync(string userId, CancellationToken cancellationToken);
}
