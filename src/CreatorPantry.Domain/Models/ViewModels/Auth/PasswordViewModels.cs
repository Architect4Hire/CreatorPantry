namespace CreatorPantry.Domain.Models.ViewModels.Auth;

/// <summary>HTTP input for <c>POST /api/v1/auth/password-reset</c>.</summary>
public sealed class RequestPasswordResetViewModel
{
    public string Email { get; set; } = string.Empty;
}

/// <summary>HTTP input for <c>POST /api/v1/auth/password-reset/complete</c>.</summary>
public sealed class CompletePasswordResetViewModel
{
    public string Email { get; set; } = string.Empty;

    /// <summary>The token from the reset message, as delivered.</summary>
    public string Token { get; set; } = string.Empty;

    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>Input for changing the authenticated caller's password.</summary>
public sealed class ChangePasswordViewModel
{
    public string CurrentPassword { get; set; } = string.Empty;

    public string NewPassword { get; set; } = string.Empty;
}
