using System.Security.Claims;
using System.Text.Encodings.Web;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Auth.Facade;
using CreatorPantry.Domain.Modules.Auth.Managers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CreatorPantry.ApiService.Authorization;

/// <summary>
/// Authenticates a machine caller with a hashed, rotatable ops API key (baseline B-14).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Registered beside the JWT scheme, never as the default.</strong> The gateway's internal token stays
/// the default scheme, so this one runs only where a policy names it — which is how an ops key is confined to
/// <c>/api/v1/ops/*</c> without every product route having to remember to exclude it.
/// </para>
/// <para>
/// <strong>Its own <c>Authorization</c> scheme token, not <c>Bearer</c>.</strong> Sharing the token with the
/// gateway's JWT would make a misrouted credential look plausible to the wrong validator; with a distinct
/// token, presenting one where the other is expected simply fails.
/// </para>
/// <para>
/// <strong>An ops key is never a creator.</strong> The principal it produces carries a client id and its
/// scopes, and deliberately no <c>sub</c> that any product route would read as a user and no workspace role.
/// It also carries no <c>token_use</c> claim, so every policy built on <c>UserPolicy()</c> — which requires
/// <c>token_use=user</c> — refuses it even if a route were mis-annotated.
/// </para>
/// </remarks>
public sealed class OpsApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOpsApiClientFacade clients) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header))
        {
            return AuthenticateResult.NoResult();
        }

        var value = header.ToString();
        var token = $"{OpsApiKeyPolicy.Scheme} ";

        if (!value.StartsWith(token, StringComparison.OrdinalIgnoreCase))
        {
            // Some other scheme's credential. NoResult rather than Fail, so the response is a plain challenge
            // rather than a message telling an unauthenticated caller which scheme this route wanted.
            return AuthenticateResult.NoResult();
        }

        var client = await clients.AuthenticateAsync(value[token.Length..].Trim(), Context.RequestAborted);

        if (client is null)
        {
            // One message for an unknown prefix, a wrong secret, and a revoked client alike.
            return AuthenticateResult.Fail("Invalid ops API key.");
        }

        var identity = new ClaimsIdentity(OpsApiKeyPolicy.Scheme, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, client.ClientId.ToString("D")));
        identity.AddClaim(new Claim(OpsClaims.ClientName, client.Name));

        foreach (var scope in client.Scopes)
        {
            identity.AddClaim(new Claim(OpsClaims.Scope, scope));
        }

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), OpsApiKeyPolicy.Scheme));
    }

    /// <summary>Announces the scheme on a 401, so a client knows what to present.</summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = OpsApiKeyPolicy.Scheme;

        return base.HandleChallengeAsync(properties);
    }
}

/// <summary>Reads the authenticated ops client back off a request.</summary>
public static class OpsApiKeyPrincipal
{
    /// <summary>
    /// The acting client, for the audit trail. Throws if called outside the ops policy, which cannot happen:
    /// every route that reads it is behind <see cref="AuthorizationPolicies.Ops"/>, and that policy admits
    /// only a principal this handler issued.
    /// </summary>
    public static PlatformActor OpsActor(this ClaimsPrincipal user) =>
        new(PlatformAuditActorType.OpsClient,
            user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? throw new InvalidOperationException("An ops principal carries no client id."),
            user.FindFirst(OpsClaims.ClientName)?.Value ?? "unknown");
}
