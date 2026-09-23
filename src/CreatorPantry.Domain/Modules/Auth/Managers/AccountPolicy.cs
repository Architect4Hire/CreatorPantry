namespace CreatorPantry.Domain.Modules.Auth.Managers;

/// <summary>
/// Account limits shared by request validation and ASP.NET Core Identity options. Passwords follow
/// NIST SP 800-63B: a length range with no composition rules.
/// </summary>
public static class AccountPolicy
{
    public const int PasswordMinLength = 12;
    public const int PasswordMaxLength = 128;

    /// <summary>Matches the Identity column length for Email and UserName.</summary>
    public const int EmailMaxLength = 256;

    public const int DisplayNameMaxLength = 100;

    /// <summary>Failed password attempts before the account is locked out.</summary>
    public const int MaxFailedSignInAttempts = 5;

    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    /// <summary>How long a password-reset token remains valid. Tokens are also single-use.</summary>
    public static readonly TimeSpan PasswordResetTokenLifetime = TimeSpan.FromHours(1);
}
