namespace CreatorPantry.Domain.Models.ServiceModels.Auth;

/// <summary>
/// The registration response. Identical for new and already-registered emails so the response never
/// reveals whether an account exists; the account becomes usable after email confirmation.
/// </summary>
public sealed record RegistrationServiceModel(string Status)
{
    public const string PendingConfirmationStatus = "pending_confirmation";

    public static RegistrationServiceModel PendingConfirmation { get; } = new(PendingConfirmationStatus);
}
