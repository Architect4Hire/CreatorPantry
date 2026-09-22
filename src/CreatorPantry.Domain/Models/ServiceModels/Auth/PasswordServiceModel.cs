namespace CreatorPantry.Domain.Models.ServiceModels.Auth;

/// <summary>The outcome of a password operation.</summary>
public sealed record PasswordServiceModel(string Status)
{
    /// <summary>Returned for every reset request, whether or not the email belongs to an account.</summary>
    public static PasswordServiceModel ResetRequested { get; } = new("reset_requested");

    public static PasswordServiceModel ResetCompleted { get; } = new("password_reset");

    public static PasswordServiceModel Changed { get; } = new("password_changed");
}
