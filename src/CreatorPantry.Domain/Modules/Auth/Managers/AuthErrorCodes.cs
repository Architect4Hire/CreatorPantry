namespace CreatorPantry.Domain.Modules.Auth.Managers;

/// <summary>Stable error codes for authentication operations. Renaming a code is a breaking API change.</summary>
public static class AuthErrorCodes
{
    public const string RegistrationInvalid = "auth.registration.invalid";

    public const string PasswordResetInvalid = "auth.password_reset.invalid";

    /// <summary>Unknown email, wrong, expired, or used token: deliberately indistinguishable.</summary>
    public const string PasswordResetInvalidToken = "auth.password_reset.invalid_token";

    public const string PasswordChangeInvalid = "auth.password_change.invalid";

    /// <summary>The authenticated caller's account no longer exists.</summary>
    public const string AccountNotFound = "auth.account.not_found";

    public const string SignInInvalidRequest = "auth.signin.invalid_request";

    /// <summary>Unknown email, wrong password, or locked account: deliberately indistinguishable.</summary>
    public const string SignInFailed = "auth.signin.failed";

    /// <summary>Reported only after the correct password, which proves ownership of the address.</summary>
    public const string SignInEmailUnconfirmed = "auth.signin.email_unconfirmed";

    /// <summary>The session's user no longer exists or its security stamp changed (password change or reset).</summary>
    public const string SessionInvalid = "auth.session.invalid";
}
