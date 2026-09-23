namespace CreatorPantry.Domain.Modules.Auth.Managers;

/// <summary>Internal input from the gateway: credentials a browser submitted to <c>/bff/login</c>.</summary>
public sealed class VerifyCredentialsViewModel
{
    public string Email { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}

/// <summary>Internal input from the gateway: the session facts it re-checks periodically.</summary>
public sealed class ValidateSessionViewModel
{
    public string UserId { get; set; } = string.Empty;

    public string SecurityStamp { get; set; } = string.Empty;
}
