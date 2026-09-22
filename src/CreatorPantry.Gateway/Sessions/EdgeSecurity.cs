using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using CreatorPantry.ServiceDefaults;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using StackExchange.Redis;

namespace CreatorPantry.Gateway.Sessions;

/// <summary>Browser session and edge protections for the gateway (baselines B-02 and B-13).</summary>
public static partial class EdgeSecurity
{
    public const string SpaCorsPolicy = "spa";

    public static IHostApplicationBuilder AddBffSession(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        services.AddOptions<GatewaySessionOptions>()
            .Bind(builder.Configuration.GetSection(GatewaySessionOptions.SectionName))
            .Validate(GatewaySessionOptions.IsValid, GatewaySessionOptions.ValidationMessage)
            .ValidateOnStart();

        // Every gateway-generated failure (framework 4xx, cookie 401/403, 500, YARP 502/504) is ProblemDetails
        // with the same code/traceId shape the API uses.
        services.AddProblemDetails(options => options.CustomizeProblemDetails = EdgeHardening.AddStatusCode);

        // Session tickets and the Data Protection key ring live in Redis so every gateway instance shares them.
        builder.AddRedisDistributedCache("cache");
        services.AddDataProtection().SetApplicationName("CreatorPantry.Gateway");
        services.AddOptions<KeyManagementOptions>().Configure<IServiceProvider>((options, provider) =>
            options.XmlRepository = new RedisXmlRepository(
                () => provider.GetRequiredService<IConnectionMultiplexer>().GetDatabase(), "bff:data-protection-keys"));

        services.AddSingleton<ITicketStore, DistributedTicketStore>();
        services.AddScoped<SessionValidator>();
        // No retries: repeating a credential check could double-count failed attempts toward lockout.
        // RemoveAllResilienceHandlers is marked experimental (EXTEXP0001); it is the supported opt-out from the
        // resilience defaults ServiceDefaults applies to every HttpClient.
#pragma warning disable EXTEXP0001
        services.AddHttpClient<ApiSessionClient>(client =>
            {
                client.BaseAddress = new Uri("https+http://api");
                client.Timeout = TimeSpan.FromSeconds(10);
            })
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = GatewaySessionOptions.CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.Path = "/";
                options.Cookie.IsEssential = true;
                options.SlidingExpiration = true;

                // An API-style edge: no redirects to login pages.
                options.Events.OnRedirectToLogin = context => SetStatus(context.Response, StatusCodes.Status401Unauthorized);
                options.Events.OnRedirectToAccessDenied = context => SetStatus(context.Response, StatusCodes.Status403Forbidden);
                options.Events.OnValidatePrincipal = context =>
                    context.HttpContext.RequestServices.GetRequiredService<SessionValidator>().ValidateAsync(context);
            });
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<ITicketStore, Microsoft.Extensions.Options.IOptions<GatewaySessionOptions>>((cookie, store, session) =>
            {
                cookie.SessionStore = store;
                cookie.ExpireTimeSpan = session.Value.IdleTimeout;
            });

        services.AddAntiforgery(options =>
        {
            options.HeaderName = GatewaySessionOptions.AntiforgeryHeaderName;
            options.Cookie.Name = GatewaySessionOptions.AntiforgeryCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
        });

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(BffEndpoints.LoginRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter =
                    context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                        ? ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : "60";
                return new ValueTask(BffEndpoints
                    .Problem(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many requests. Try again shortly.")
                    .ExecuteAsync(context.HttpContext));
            };
        });

        var origins = ValidateOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? []);
        services.AddCors(options => options.AddPolicy(SpaCorsPolicy, policy => policy
            .WithOrigins(origins)
            .AllowCredentials()
            .WithHeaders("Content-Type", GatewaySessionOptions.AntiforgeryHeaderName, "Idempotency-Key", "If-Match")
            .WithExposedHeaders("Idempotent-Replayed", "ETag")
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")));

        return builder;
    }

    /// <summary>
    /// The edge pipeline, in order: trusted forwarded headers, error handling, CORS, security headers, body
    /// limit, rate limits, session, blocked-route check, antiforgery.
    /// </summary>
    public static WebApplication UseEdgeSecurity(this WebApplication app)
    {
        // First, so the rate limiter, cookies (Secure), and proxied X-Forwarded-* see the real client and scheme.
        app.UseTrustedForwardedHeaders();

        // Bad requests the framework throws (e.g. unreadable JSON; minimal APIs throw them in Development) keep
        // their own status instead of becoming a 500.
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            StatusCodeSelector = exception => exception is BadHttpRequestException badRequest
                ? badRequest.StatusCode
                : StatusCodes.Status500InternalServerError,
        });
        app.UseStatusCodePages();

        // CORS before any early rejection (413, 429, 400 csrf.invalid) so the cross-origin SPA can read them.
        app.UseCors(SpaCorsPolicy);
        app.UseSecurityHeaders(EdgeHardening.ApiContentSecurityPolicy);
        app.UseRequestBodyLimit();
        app.UseRateLimiter();
        app.UseAuthentication();

        // Internal API routes (gateway service identity only) and development tooling are never reachable
        // from a browser.
        app.Use(async (context, next) =>
        {
            if (BlockedApiPath().IsMatch(context.Request.Path.Value ?? string.Empty))
            {
                await BffEndpoints.Problem(StatusCodes.Status404NotFound, "not_found", "Not Found").ExecuteAsync(context);
                return;
            }

            await next(context);
        });

        // Every unsafe browser request to the session routes or the API must carry the antiforgery token.
        app.Use(async (context, next) =>
        {
            if (RequiresAntiforgery(context.Request))
            {
                try
                {
                    await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
                }
                catch (AntiforgeryValidationException)
                {
                    await BffEndpoints.Problem(StatusCodes.Status400BadRequest, "csrf.invalid",
                        "The request is missing a valid antiforgery token.").ExecuteAsync(context);
                    return;
                }
            }

            await next(context);
        });

        return app;
    }

    /// <summary>
    /// Exact origins only: credentials are allowed, so a wildcard, a path, or a malformed origin would widen
    /// who can make authenticated calls. Invalid configuration stops the gateway from starting.
    /// </summary>
    internal static string[] ValidateOrigins(string[] configured)
    {
        foreach (var origin in configured)
        {
            var valid = Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
                && string.IsNullOrEmpty(uri.UserInfo)
                && !origin.Contains('*', StringComparison.Ordinal)
                && origin.TrimEnd('/') == uri.GetLeftPart(UriPartial.Authority);

            if (!valid)
            {
                throw new InvalidOperationException(
                    $"Cors:AllowedOrigins contains '{origin}'. Each entry must be an exact http(s) origin such as https://app.example.com.");
            }
        }

        return [.. configured.Select(origin => origin.TrimEnd('/'))];
    }

    private static bool RequiresAntiforgery(HttpRequest request) =>
        !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
            || HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method))
        && (request.Path.StartsWithSegments("/bff") || request.Path.StartsWithSegments("/api"));

    private static Task SetStatus(HttpResponse response, int status)
    {
        response.StatusCode = status;
        return Task.CompletedTask;
    }

    [GeneratedRegex(@"^/+api/+([^/]+/+internal|dev)(/|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockedApiPath();
}
