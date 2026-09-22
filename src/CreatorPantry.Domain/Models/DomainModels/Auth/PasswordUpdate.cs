namespace CreatorPantry.Domain.Models.DomainModels.Auth;

/// <summary>An account as Business needs it; no credential material.</summary>
public sealed record UserAccount(string UserId, string Email, bool EmailConfirmed);

public enum PasswordUpdateStatus
{
    Succeeded,

    /// <summary>The reset token is wrong, expired, already used, or unreadable.</summary>
    InvalidToken,

    IncorrectCurrentPassword,

    /// <summary>The new password failed Identity's password validators.</summary>
    InvalidPassword,

    NotFound,
}

public sealed record PasswordUpdateResult(PasswordUpdateStatus Status, IReadOnlyList<string> Errors)
{
    public static PasswordUpdateResult Of(PasswordUpdateStatus status) => new(status, []);

    public static PasswordUpdateResult InvalidPassword(IReadOnlyList<string> errors) => new(PasswordUpdateStatus.InvalidPassword, errors);
}
