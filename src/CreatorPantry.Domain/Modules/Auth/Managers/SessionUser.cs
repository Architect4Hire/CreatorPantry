namespace CreatorPantry.Domain.Modules.Auth.Managers;

/// <param name="CanSignIn">False when the account is locked out or its email is unconfirmed.</param>
public sealed record SessionUser(
    string UserId, string DisplayName, IReadOnlyList<string> Roles, string SecurityStamp, bool CanSignIn = true);

public enum CredentialCheckStatus
{
    Succeeded,

    /// <summary>Unknown email or wrong password.</summary>
    Invalid,

    LockedOut,

    /// <summary>The password was correct but the email is not confirmed.</summary>
    EmailUnconfirmed,
}

public sealed record CredentialCheckResult(CredentialCheckStatus Status, SessionUser? User)
{
    public static CredentialCheckResult Of(CredentialCheckStatus status) => new(status, null);
}
