using CreatorPantry.Domain.Modules.Auth.Data;
using CreatorPantry.Domain.Modules.Auth.Managers;

namespace CreatorPantry.Domain.Modules.Auth.Business;

internal interface IOpsApiClientBusiness
{
    Task<OpsApiClientServiceModel?> AuthenticateAsync(string? credential, CancellationToken cancellationToken);
}

internal sealed class OpsApiClientBusiness(IOpsApiClientDataLayer dataLayer) : IOpsApiClientBusiness
{
    /// <summary>Fixed material the miss path hashes against, so a lookup miss costs what a hit costs.</summary>
    /// <remarks>Never a real credential: nothing can present a secret that verifies against it.</remarks>
    private static readonly byte[] DecoySalt = new byte[OpsApiKeyPolicy.SaltLength];

    /// <inheritdoc cref="DecoySalt"/>
    private static readonly byte[] DecoyHash = new byte[OpsApiKeyPolicy.HashLength];

    public async Task<OpsApiClientServiceModel?> AuthenticateAsync(string? credential, CancellationToken cancellationToken)
    {
        var parts = OpsApiKeyHasher.Parse(credential);
        if (parts is null)
        {
            return null;
        }

        var client = await dataLayer.FindByPrefixAsync(parts.Prefix, cancellationToken);

        // Hash before branching on whether a client was found, so an unknown prefix costs the same work as a
        // wrong secret. Returning early on null would leave the two separable by timing even though the
        // comparison itself is constant-time, and a prefix is the half of the credential an attacker can
        // enumerate cheaply.
        var verified = client is not null
            && OpsApiKeyHasher.Verify(client.KeySalt, client.KeyHash, parts.Secret);

        _ = client is null && OpsApiKeyHasher.Verify(DecoySalt, DecoyHash, parts.Secret);

        // One "no" for an unknown prefix, a wrong secret, and a revoked client alike: which of the three it
        // was is not something an unauthenticated caller gets to learn.
        if (client is null || client.RevokedAt is not null || !verified)
        {
            return null;
        }

        await dataLayer.StampUsedAsync(client, cancellationToken);

        return new OpsApiClientServiceModel(
            client.Id,
            client.Name,
            [.. client.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]);
    }
}
