using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CreatorPantry.ServiceDefaults;

/// <summary>Request-body limits and browser security headers shared by HTTP hosts.</summary>
public static class EdgeHardening
{
    public const string MaxRequestBodyConfigurationKey = "Limits:MaxRequestBodyBytes";

    /// <summary>Default body limit for every route. Upload routes raise it with <c>[RequestSizeLimit]</c>.</summary>
    public const long DefaultMaxRequestBodyBytes = 4 * 1024 * 1024;

    public const string RequestTooLargeCode = "request_too_large";

    /// <summary>
    /// The stable code for a framework-generated problem (no application code), shared by every HTTP host so
    /// gateway- and API-generated problems look the same to a browser. Application codes are dotted
    /// (<c>area.reason</c>); these HTTP-level codes are single snake_case words.
    /// </summary>
    public static string CodeForStatus(int status) => status switch
    {
        >= StatusCodes.Status500InternalServerError and not StatusCodes.Status502BadGateway
            and not StatusCodes.Status503ServiceUnavailable and not StatusCodes.Status504GatewayTimeout => "internal_error",
        StatusCodes.Status502BadGateway or StatusCodes.Status504GatewayTimeout => "upstream_unavailable",
        StatusCodes.Status503ServiceUnavailable => "service_unavailable",
        StatusCodes.Status400BadRequest => "bad_request",
        StatusCodes.Status401Unauthorized => "unauthorized",
        StatusCodes.Status403Forbidden => "forbidden",
        StatusCodes.Status404NotFound => "not_found",
        StatusCodes.Status405MethodNotAllowed => "method_not_allowed",
        StatusCodes.Status413PayloadTooLarge => RequestTooLargeCode,
        StatusCodes.Status415UnsupportedMediaType => "unsupported_media_type",
        StatusCodes.Status429TooManyRequests => "rate_limited",
        _ => $"http_{status}",
    };

    /// <summary>Adds <see cref="CodeForStatus"/> to framework-generated problems that carry no code.</summary>
    public static void AddStatusCode(ProblemDetailsContext context)
    {
        if (!context.ProblemDetails.Extensions.ContainsKey("code"))
        {
            context.ProblemDetails.Extensions["code"] =
                CodeForStatus(context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode);
        }
    }

    /// <summary>
    /// Applies the configured body limit (or an endpoint's own <c>[RequestSizeLimit]</c>) to every request.
    /// A declared <c>Content-Length</c> over the limit is rejected immediately with 413 ProblemDetails; a body
    /// that grows past it while streaming is stopped by the server.
    /// </summary>
    public static WebApplication UseRequestBodyLimit(this WebApplication app)
    {
        var limit = app.Configuration.GetValue<long?>(MaxRequestBodyConfigurationKey) ?? DefaultMaxRequestBodyBytes;
        if (limit <= 0)
        {
            throw new InvalidOperationException($"{MaxRequestBodyConfigurationKey} must be a positive number of bytes.");
        }

        app.Use(async (context, next) =>
        {
            var endpointLimit = context.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>();
            var effective = endpointLimit is null ? limit : endpointLimit.MaxRequestBodySize;

            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            {
                feature.MaxRequestBodySize = effective;
            }

            if (effective is not null && context.Request.ContentLength > effective)
            {
                await WriteProblemAsync(context, StatusCodes.Status413PayloadTooLarge, RequestTooLargeCode,
                    $"The request body exceeds the {effective}-byte limit.");
                return;
            }

            await next(context);
        });

        return app;
    }

    /// <summary>
    /// Adds baseline browser security headers to every response (set as the response starts, so proxied
    /// responses are covered too) and HSTS outside Development.
    /// </summary>
    public static WebApplication UseSecurityHeaders(this WebApplication app, string contentSecurityPolicy)
    {
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.ContentSecurityPolicy = contentSecurityPolicy;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                headers["Permissions-Policy"] =
                    "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";
                headers.Remove("Server");
                headers.Remove("X-Powered-By");
                return Task.CompletedTask;
            });

            return next(context);
        });

        return app;
    }

    /// <summary>CSP for hosts that return data, never documents: nothing may load and nothing may frame it.</summary>
    public const string ApiContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    internal static void ConfigureServerHeader(IServiceCollection services) =>
        services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(options => options.AddServerHeader = false);

    private static Task WriteProblemAsync(HttpContext context, int status, string code, string title)
    {
        context.Response.StatusCode = status;
        var problems = context.RequestServices.GetService<IProblemDetailsService>();
        if (problems is null)
        {
            return Task.CompletedTask;
        }

        return problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = { Status = status, Title = title, Extensions = { ["code"] = code } },
        }).AsTask();
    }
}
