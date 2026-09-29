using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Managers;

namespace CreatorPantry.Domain.Modules.Auth.Data;

internal interface IOpsApiClientDataLayer
{
    /// <summary>The client a key prefix identifies, or null.</summary>
    Task<OpsApiClient?> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken);

    /// <summary>
    /// Records that this client just authenticated (B-14: keys are audited on use).
    /// </summary>
    /// <remarks>
    /// Best-effort and deliberately swallowing its own failure: a request that authenticated correctly must
    /// not be refused because a bookkeeping column could not be written. Two concurrent requests racing to
    /// stamp the same instant is a last-write-wins the column is happy with.
    /// </remarks>
    Task StampUsedAsync(OpsApiClient client, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts the client, or rotates its key when the configured key no longer matches the stored hash.
    /// </summary>
    Task<OpsApiClientSeedOutcome> UpsertAsync(
        string name, string keyPrefix, byte[] salt, byte[] hash, string scopes, CancellationToken cancellationToken);
}

/// <summary>What the seeder did.</summary>
internal enum OpsApiClientSeedOutcome
{
    Unchanged = 0,
    Created = 1,
    Rotated = 2,
}

internal sealed class OpsApiClientDataLayer(IOpsApiClientRepository repository, IClock clock) : IOpsApiClientDataLayer
{
    public Task<OpsApiClient?> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken) =>
        repository.FindByPrefixAsync(keyPrefix, cancellationToken);

    public async Task StampUsedAsync(OpsApiClient client, CancellationToken cancellationToken)
    {
        client.LastUsedAt = clock.UtcNow;

        try
        {
            await repository.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // See the interface remarks: authentication already succeeded.
        }
    }

    public async Task<OpsApiClientSeedOutcome> UpsertAsync(
        string name, string keyPrefix, byte[] salt, byte[] hash, string scopes, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var existing = await repository.FindByNameAsync(name, cancellationToken);

        if (existing is null)
        {
            repository.Add(new OpsApiClient
            {
                Id = Guid.NewGuid(),
                Name = name,
                KeyPrefix = keyPrefix,
                KeySalt = salt,
                KeyHash = hash,
                Scopes = scopes,
                CreatedAt = now,
            });

            await repository.SaveChangesAsync(cancellationToken);

            return OpsApiClientSeedOutcome.Created;
        }

        var unchanged = existing.KeyPrefix == keyPrefix
            && existing.KeyHash.AsSpan().SequenceEqual(hash)
            && existing.Scopes == scopes
            && existing.RevokedAt is null;

        if (unchanged)
        {
            return OpsApiClientSeedOutcome.Unchanged;
        }

        existing.KeyPrefix = keyPrefix;
        existing.KeySalt = salt;
        existing.KeyHash = hash;
        existing.Scopes = scopes;
        existing.RotatedAt = now;

        // Re-seeding a key restores a client that had been switched off; there is one source of truth for
        // whether this automation exists, and it is the secret store.
        existing.RevokedAt = null;

        await repository.SaveChangesAsync(cancellationToken);

        return OpsApiClientSeedOutcome.Rotated;
    }
}
