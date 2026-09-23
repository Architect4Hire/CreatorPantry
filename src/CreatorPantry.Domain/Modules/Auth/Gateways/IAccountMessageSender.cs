namespace CreatorPantry.Domain.Modules.Auth.Gateways;

/// <summary>
/// Delivers account-security messages. A real provider adapter must queue durably and return promptly, so
/// delivery latency neither blocks the request nor reveals whether an account exists.
/// </summary>
public interface IAccountMessageSender
{
    Task SendAsync(AccountMessage message, CancellationToken cancellationToken);
}
