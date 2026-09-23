namespace CreatorPantry.Domain.Modules.Auth.Managers;

public enum EmailConfirmationStatus
{
    /// <summary>Also returned when the account's email was already confirmed: confirming is idempotent.</summary>
    Succeeded,

    /// <summary>The confirmation token is wrong, expired, or unreadable.</summary>
    InvalidToken,

    NotFound,
}

public sealed record EmailConfirmationResult(EmailConfirmationStatus Status)
{
    public static EmailConfirmationResult Of(EmailConfirmationStatus status) => new(status);
}
