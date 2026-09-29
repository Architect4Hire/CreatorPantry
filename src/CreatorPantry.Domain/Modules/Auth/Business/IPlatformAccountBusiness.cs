using CreatorPantry.Domain.Modules.Auth.Data;

namespace CreatorPantry.Domain.Modules.Auth.Business;

internal interface IPlatformAccountBusiness
{
    Task<bool> AccountExistsAsync(string accountId, CancellationToken cancellationToken);
}

internal sealed class PlatformAccountBusiness(IAuthDataLayer dataLayer) : IPlatformAccountBusiness
{
    public async Task<bool> AccountExistsAsync(string accountId, CancellationToken cancellationToken) =>
        !string.IsNullOrWhiteSpace(accountId)
        && await dataLayer.FindAccountByIdAsync(accountId, cancellationToken) is not null;
}
