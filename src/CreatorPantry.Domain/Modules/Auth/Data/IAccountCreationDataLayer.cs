namespace CreatorPantry.Domain.Modules.Auth.Data;

/// <inheritdoc cref="Facade.IAccountCreationFacade"/>
internal interface IAccountCreationDataLayer
{
    Task<DateTimeOffset?> FindCreatedAtAsync(string accountId, CancellationToken cancellationToken);
}

internal sealed class AccountCreationDataLayer(IAccountCreationRepository accounts) : IAccountCreationDataLayer
{
    public Task<DateTimeOffset?> FindCreatedAtAsync(string accountId, CancellationToken cancellationToken) =>
        accounts.FindCreatedAtAsync(accountId, cancellationToken);
}
