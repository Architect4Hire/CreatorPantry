namespace CreatorPantry.Domain.Modules.Auth.Managers;

/// <summary>HTTP input for <c>POST /api/v1/auth/confirm-email</c>.</summary>
public sealed class ConfirmEmailViewModel
{
    public string Email { get; set; } = string.Empty;

    /// <summary>The token from the confirmation message, as delivered.</summary>
    public string Token { get; set; } = string.Empty;
}
