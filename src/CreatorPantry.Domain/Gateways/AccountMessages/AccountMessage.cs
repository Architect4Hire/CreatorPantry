namespace CreatorPantry.Domain.Gateways.AccountMessages;

public enum AccountMessageKind
{
    PasswordReset,
    PasswordChanged,
}

/// <summary>
/// An account-security message for one user. It may carry a secret token, so <see cref="ToString"/> omits
/// the token and email: never log the properties individually either.
/// </summary>
public sealed class AccountMessage
{
    private AccountMessage(AccountMessageKind kind, string userId, string email, DateTimeOffset createdAt,
        string? token, DateTimeOffset? expiresAt)
    {
        Kind = kind;
        RecipientUserId = userId;
        RecipientEmail = email;
        CreatedAt = createdAt;
        Token = token;
        ExpiresAt = expiresAt;
    }

    public AccountMessageKind Kind { get; }

    public string RecipientUserId { get; }

    public string RecipientEmail { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// The URL-safe reset token. A delivery adapter must place it in the link's fragment (<c>#token=</c>)
    /// so it never reaches server logs or Referer headers.
    /// </summary>
    public string? Token { get; }

    public DateTimeOffset? ExpiresAt { get; }

    public static AccountMessage PasswordReset(string userId, string email, string token, DateTimeOffset createdAt, DateTimeOffset expiresAt) =>
        new(AccountMessageKind.PasswordReset, userId, email, createdAt, token, expiresAt);

    public static AccountMessage PasswordChanged(string userId, string email, DateTimeOffset createdAt) =>
        new(AccountMessageKind.PasswordChanged, userId, email, createdAt, null, null);

    public override string ToString() => $"AccountMessage {{ Kind = {Kind}, RecipientUserId = {RecipientUserId} }}";
}
