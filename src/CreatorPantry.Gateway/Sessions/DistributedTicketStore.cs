using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Gateway.Sessions;

/// <summary>
/// Keeps session tickets server-side (Redis in every deployed and Aspire environment). The browser cookie
/// carries only the protected session id; deleting the entry revokes the session everywhere.
/// </summary>
internal sealed class DistributedTicketStore(IDistributedCache cache, IOptions<GatewaySessionOptions> options) : ITicketStore
{
    private const string KeyPrefix = "bff:session:";

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var sessionId = ticket.Principal.FindFirst(SessionClaims.SessionId)?.Value
            ?? throw new InvalidOperationException("A session ticket must carry a session id.");

        await RenewAsync(sessionId, ticket);
        return sessionId;
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        var authenticatedAt = SessionClaims.ReadTime(ticket.Principal, SessionClaims.AuthenticatedAt)
            ?? throw new InvalidOperationException("A session ticket must carry its authentication time.");

        return cache.SetAsync(KeyPrefix + key, TicketSerializer.Default.Serialize(ticket), new DistributedCacheEntryOptions
        {
            SlidingExpiration = options.Value.IdleTimeout,
            AbsoluteExpiration = authenticatedAt + options.Value.AbsoluteLifetime,
        });
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var bytes = await cache.GetAsync(KeyPrefix + key);
        return bytes is null ? null : TicketSerializer.Default.Deserialize(bytes);
    }

    public Task RemoveAsync(string key) => cache.RemoveAsync(KeyPrefix + key);
}
