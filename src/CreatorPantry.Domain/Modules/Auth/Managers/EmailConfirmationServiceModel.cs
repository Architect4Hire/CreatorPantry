namespace CreatorPantry.Domain.Modules.Auth.Managers;

/// <summary>The outcome of an email confirmation.</summary>
public sealed record EmailConfirmationServiceModel(string Status)
{
    public static EmailConfirmationServiceModel Confirmed { get; } = new("email_confirmed");
}
