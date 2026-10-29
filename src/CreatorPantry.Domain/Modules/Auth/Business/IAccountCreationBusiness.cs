using CreatorPantry.Domain.Modules.Auth.Data;

namespace CreatorPantry.Domain.Modules.Auth.Business;

/// <inheritdoc cref="Facade.IAccountCreationFacade"/>
internal interface IAccountCreationBusiness
{
    Task<DateTimeOffset?> FindCreatedAtAsync(string accountId, CancellationToken cancellationToken);
}

internal sealed class AccountCreationBusiness(IAccountCreationDataLayer dataLayer) : IAccountCreationBusiness
{
    public Task<DateTimeOffset?> FindCreatedAtAsync(string accountId, CancellationToken cancellationToken) =>
        dataLayer.FindCreatedAtAsync(accountId, cancellationToken);
}
