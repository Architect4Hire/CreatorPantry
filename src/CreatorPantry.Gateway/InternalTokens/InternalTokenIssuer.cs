using System.Security.Claims;
using System.Security.Cryptography;
using CreatorPantry.ServiceDefaults;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CreatorPantry.Gateway.InternalTokens;

/// <summary>The authenticated browser session the gateway forwards on behalf of.</summary>
public sealed record SessionPrincipal(string UserId, string SessionId, IReadOnlyCollection<string> Roles);

/// <summary>Mints the short-lived, gateway-signed tokens the API accepts (baseline B-13).</summary>
public interface IInternalTokenIssuer
{
    /// <summary>A token acting for a signed-in user (<c>token_use=user</c>).</summary>
    string Issue(SessionPrincipal principal);

    /// <summary>A token for the gateway itself (<c>token_use=service</c>), used only on internal session routes.</summary>
    string IssueService();
}

// A process-lifetime singleton. The ECDsa key is deliberately not disposed: IdentityModel caches signature
// providers process-wide by key thumbprint, so disposing it could break a cached provider still in use.
internal sealed class InternalTokenIssuer : IInternalTokenIssuer
{
    private readonly JsonWebTokenHandler _handler = new();
    private readonly SigningCredentials _credentials;
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _time;

    public InternalTokenIssuer(IOptions<InternalTokenOptions> options, TimeProvider time)
    {
        var key = ECDsa.Create();
        key.ImportFromPem(options.Value.SigningKeyPem);
        _credentials = new SigningCredentials(new ECDsaSecurityKey(key), SecurityAlgorithms.EcdsaSha256);
        _lifetime = options.Value.Lifetime;
        _time = time;
    }

    public string Issue(SessionPrincipal principal)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, principal.UserId),
            new(InternalTokenDefaults.SessionIdClaim, principal.SessionId),
            new(InternalTokenDefaults.TokenUseClaim, InternalTokenDefaults.UserTokenUse),
        };
        claims.AddRange(principal.Roles.Select(role => new Claim(InternalTokenDefaults.RoleClaim, role)));

        return Create(claims);
    }

    public string IssueService() => Create(
    [
        new Claim(JwtRegisteredClaimNames.Sub, InternalTokenDefaults.GatewayServiceSubject),
        new Claim(InternalTokenDefaults.TokenUseClaim, InternalTokenDefaults.ServiceTokenUse),
    ]);

    private string Create(List<Claim> claims)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        claims.Add(new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")));

        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = InternalTokenDefaults.Issuer,
            Audience = InternalTokenDefaults.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now,
            Expires = now + _lifetime,
            SigningCredentials = _credentials,
        });
    }
}
