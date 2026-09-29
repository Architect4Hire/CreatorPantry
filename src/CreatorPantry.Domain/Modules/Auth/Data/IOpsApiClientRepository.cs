using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Auth.Data;

internal interface IOpsApiClientRepository
{
    /// <summary>The client a presented key's prefix belongs to, revoked or not. Tracked: the caller stamps use.</summary>
    Task<OpsApiClient?> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken);

    /// <summary>The client with this name, for the seeder's insert-or-rotate decision.</summary>
    Task<OpsApiClient?> FindByNameAsync(string name, CancellationToken cancellationToken);

    void Add(OpsApiClient client);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

internal sealed class OpsApiClientRepository(CreatorPantryDbContext context) : IOpsApiClientRepository
{
    public Task<OpsApiClient?> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken) =>
        context.OpsApiClients.SingleOrDefaultAsync(client => client.KeyPrefix == keyPrefix, cancellationToken);

    public Task<OpsApiClient?> FindByNameAsync(string name, CancellationToken cancellationToken) =>
        context.OpsApiClients.SingleOrDefaultAsync(client => client.Name == name, cancellationToken);

    public void Add(OpsApiClient client) => context.OpsApiClients.Add(client);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => context.SaveChangesAsync(cancellationToken);
}
