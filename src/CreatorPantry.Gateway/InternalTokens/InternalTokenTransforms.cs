using System.Net.Http.Headers;
using System.Security.Claims;
using CreatorPantry.ServiceDefaults;
using Yarp.ReverseProxy.Transforms;

namespace CreatorPantry.Gateway.InternalTokens;

public static class InternalTokenServiceCollectionExtensions
{
    public static IServiceCollection AddInternalTokens(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<InternalTokenOptions>()
            .Bind(configuration.GetSection(InternalTokenDefaults.ConfigurationSection))
            .ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<InternalTokenOptions>, InternalTokenOptionsValidator>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IInternalTokenIssuer, InternalTokenIssuer>();

        return services;
    }

    /// <summary>
    /// Adds the gateway's credential to every proxied request. Runs after the configured transforms, which
    /// strip client-supplied <c>Authorization</c>: only a gateway-minted token can reach the API.
    /// </summary>
    public static IReverseProxyBuilder AddInternalTokenTransform(this IReverseProxyBuilder builder) =>
        builder.AddTransforms(context => context.AddRequestTransform(transform =>
        {
            transform.ProxyRequest.Headers.Authorization = null;

            if (SessionPrincipalFrom(transform.HttpContext.User) is { } principal)
            {
                var token = transform.HttpContext.RequestServices.GetRequiredService<IInternalTokenIssuer>().Issue(principal);
                transform.ProxyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return ValueTask.CompletedTask;
        }));

    /// <summary>The session principal for an authenticated gateway user; null for anonymous requests.</summary>
    internal static SessionPrincipal? SessionPrincipalFrom(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        var sessionId = user.FindFirstValue(InternalTokenDefaults.SessionIdClaim);
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(sessionId))
        {
            return null;
        }

        var roles = user.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray();
        return new SessionPrincipal(userId, sessionId, roles);
    }
}
