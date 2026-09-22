using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace CreatorPantry.Gateway.Sessions;

public sealed record LoginRequest(string? Email, string? Password);

/// <summary>What the browser may know about its session: never a token, id, or security stamp.</summary>
public sealed record SessionResponse(bool Authenticated, string? DisplayName);

public sealed record LoginResponse(bool Authenticated, string DisplayName, string RequestToken);

public sealed record AntiforgeryResponse(string RequestToken);

/// <summary>
/// Browser-facing session routes. Unsafe methods pass antiforgery validation in
/// <see cref="EdgeSecurity"/> before reaching these handlers.
/// </summary>
public static class BffEndpoints
{
    public const string LoginRateLimitPolicy = "bff-login";

    public static WebApplication MapBffEndpoints(this WebApplication app)
    {
        var bff = app.MapGroup("/bff");

        bff.MapGet("/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
        {
            NoStore(context);
            return Results.Ok(new AntiforgeryResponse(antiforgery.GetAndStoreTokens(context).RequestToken!));
        });

        bff.MapGet("/session", (HttpContext context) =>
        {
            NoStore(context);
            var user = context.User;
            return Results.Ok(user.Identity?.IsAuthenticated == true
                ? new SessionResponse(true, user.Identity.Name)
                : new SessionResponse(false, null));
        });

        bff.MapPost("/login", LoginAsync).RequireRateLimiting(LoginRateLimitPolicy);

        bff.MapPost("/logout", async (HttpContext context) =>
        {
            NoStore(context);
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); // deletes the Redis ticket
            return Results.NoContent();
        });

        return app;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request, HttpContext context, ApiSessionClient api, IAntiforgery antiforgery, TimeProvider time,
        ILogger<ApiSessionClient> logger)
    {
        NoStore(context);

        ApiSessionResult result;
        try
        {
            result = await api.VerifyCredentialsAsync(request.Email ?? string.Empty, request.Password ?? string.Empty, context.RequestAborted);
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("Sign-in unavailable: the API could not verify credentials.");
            return Problem(StatusCodes.Status503ServiceUnavailable, "auth.signin.unavailable", "Sign-in is temporarily unavailable.");
        }

        if (result.User is null)
        {
            return Problem(StatusCodes.Status400BadRequest, result.ErrorCode ?? "auth.signin.failed", result.ErrorTitle ?? "Sign-in failed.");
        }

        // A fresh session id on every sign-in prevents session fixation; any previous session is ended.
        if (context.User.Identity?.IsAuthenticated == true)
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }

        var now = time.GetUtcNow();
        var principal = SessionClaims.Create(result.User, Guid.NewGuid().ToString("N"), now, now);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = true, IssuedUtc = now, AllowRefresh = true });

        // Antiforgery tokens are bound to the user; issue one for the new identity.
        context.User = principal;
        var requestToken = antiforgery.GetAndStoreTokens(context).RequestToken!;

        return Results.Ok(new LoginResponse(true, result.User.DisplayName, requestToken));
    }

    internal static IResult Problem(int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });

    private static void NoStore(HttpContext context) => context.Response.Headers.CacheControl = "no-store";
}
