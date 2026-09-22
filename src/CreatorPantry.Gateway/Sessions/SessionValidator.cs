using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Gateway.Sessions;

/// <summary>
/// Runs on every authenticated request: enforces the absolute lifetime and, every revalidation interval,
/// confirms with the API that the user still exists, may sign in, and has the same security stamp. A password
/// change, reset, or lockout therefore ends all sessions. If the API is unreachable the session is kept, but
/// only until <see cref="GatewaySessionOptions.MaxValidationStaleness"/>; an API that refuses the check
/// (401/403/404) ends the session immediately.
/// </summary>
internal sealed class SessionValidator(
    ApiSessionClient api,
    IOptions<GatewaySessionOptions> options,
    TimeProvider time,
    ILogger<SessionValidator> logger)
{
    public async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal!;
        var now = time.GetUtcNow();

        var authenticatedAt = SessionClaims.ReadTime(principal, SessionClaims.AuthenticatedAt);
        if (authenticatedAt is null || now - authenticatedAt > options.Value.AbsoluteLifetime)
        {
            await EndAsync(context, "absolute lifetime reached");
            return;
        }

        var validatedAt = SessionClaims.ReadTime(principal, SessionClaims.ValidatedAt) ?? authenticatedAt.Value;
        if (now - validatedAt < options.Value.RevalidationInterval)
        {
            return;
        }

        var userId = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
        var stamp = principal.FindFirst(SessionClaims.SecurityStamp)?.Value ?? string.Empty;

        ApiSessionResult result;
        try
        {
            result = await api.ValidateSessionAsync(userId, stamp, context.HttpContext.RequestAborted);
        }
        catch (Exception exception) when (exception is HttpRequestException
            || (exception is TaskCanceledException && !context.HttpContext.RequestAborted.IsCancellationRequested))
        {
            // Fail open only briefly: an unvalidated session is ended once it passes the staleness bound.
            if (now - validatedAt > options.Value.MaxValidationStaleness)
            {
                await EndAsync(context, "could not be revalidated within the staleness bound");
                return;
            }

            logger.LogWarning("Session revalidation for user {UserId} deferred: the API is unavailable.", userId);
            return;
        }

        if (result.User is null)
        {
            await EndAsync(context, "security stamp no longer valid");
            return;
        }

        var sessionId = principal.FindFirst(SessionClaims.SessionId)!.Value;
        context.ReplacePrincipal(SessionClaims.Create(result.User, sessionId, authenticatedAt.Value, now));
        context.ShouldRenew = true;
    }

    private async Task EndAsync(CookieValidatePrincipalContext context, string reason)
    {
        logger.LogInformation("Ending session for user {UserId}: {Reason}.",
            context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, reason);

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); // deletes the ticket
    }
}
