namespace CreatorPantry.Domain.Models.ViewModels.Auth;

/// <summary>HTTP input for <c>POST /api/v1/auth/register</c>.</summary>
public sealed class RegisterUserViewModel
{
    public string Email { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
}
