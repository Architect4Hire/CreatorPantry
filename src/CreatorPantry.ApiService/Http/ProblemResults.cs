using System.Globalization;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CreatorPantry.ApiService.Http;

/// <summary>Maps application errors to ProblemDetails with a stable <c>code</c> and the request's <c>traceId</c>.</summary>
public static class ProblemResults
{
    public const string CodeExtension = "code";
    public const string MalformedRequestCode = "request.malformed";
    public const string InternalErrorCode = "internal_error";
    public const string NotFoundCode = "not_found";

    /// <summary>The stable code for a framework-generated problem with no application error code.</summary>
    public static string CodeForStatus(int status) => CreatorPantry.ServiceDefaults.EdgeHardening.CodeForStatus(status);

    /// <summary>
    /// The extension a 429 reads its <c>Retry-After</c> from: an instant, as a <see cref="DateTimeOffset"/>.
    /// </summary>
    /// <remarks>
    /// One well-known key rather than each controller setting the header, so a route cannot ship a 429 that
    /// tells a client to back off without saying until when. See <see cref="ProblemFor"/>.
    /// </remarks>
    public const string RetryAtExtension = "resetsAt";

    /// <summary>
    /// HTTP status for an application error code. Codes follow <c>area.reason</c>; the reason suffix selects
    /// the status so new features map consistently: <c>.not_found</c> 404 (also used to hide existence),
    /// <c>.forbidden</c> 403, <c>.conflict</c> 409 (e.g. stale concurrency token), <c>.gone</c> 410, <c>.unprocessable</c> 422 (well formed, but not something the server can accept),
    /// <c>.suspended</c> 403, <c>.exhausted</c> 429, <c>.render.failed</c> 500 (our own fault, never the caller's). Anything else is a 400 request error.
    /// </summary>
    public static int StatusFor(string code) => code switch
    {
        // The request is well formed, but the key already belongs to a different request.
        IdempotencyPolicy.KeyReusedCode => StatusCodes.Status422UnprocessableEntity,
        _ when code.EndsWith(".not_found", StringComparison.Ordinal) => StatusCodes.Status404NotFound,
        _ when code.EndsWith(".forbidden", StringComparison.Ordinal) => StatusCodes.Status403Forbidden,
        _ when code.EndsWith(".conflict", StringComparison.Ordinal) => StatusCodes.Status409Conflict,
        _ when code.EndsWith(".gone", StringComparison.Ordinal) => StatusCodes.Status410Gone,
        _ when code.EndsWith(".unprocessable", StringComparison.Ordinal) => StatusCodes.Status422UnprocessableEntity,

        // A spent allowance: temporary, self-resolving, and carrying the instant it resolves at. 429 rather
        // than 403 because the remedy is to wait rather than to be granted something, and because a client
        // that backs off on 429 is already doing the right thing here. It shares the status with the edge's
        // own rate limiter, which answers `rate_limited`; the `code` extension is what separates the two, and
        // is why every refusal here carries one.
        _ when code.EndsWith(".exhausted", StringComparison.Ordinal) => StatusCodes.Status429TooManyRequests,

        // Switched off administratively: not temporary, and deliberately not a 429 -- there is no instant to
        // wait for, so telling a client to retry would send it round a loop that cannot end differently.
        _ when code.EndsWith(".render.failed", StringComparison.Ordinal) => StatusCodes.Status500InternalServerError,
        _ when code.EndsWith(".suspended", StringComparison.Ordinal) => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status400BadRequest,
    };

    public static ObjectResult ProblemFor(this ControllerBase controller, OperationError error)
    {
        var modelState = new ModelStateDictionary();
        foreach (var (field, messages) in error.FieldErrors)
        {
            foreach (var message in messages)
            {
                modelState.AddModelError(field, message);
            }
        }

        var status = StatusFor(error.Code);
        var problem = controller.ProblemDetailsFactory.CreateValidationProblemDetails(
            controller.HttpContext, modelState, status, title: error.Message);
        problem.Extensions[CodeExtension] = error.Code;

        foreach (var (name, value) in error.Extensions ?? new Dictionary<string, object?>())
        {
            // `code` and `traceId` are this layer's to set. An application error that could overwrite either
            // would be able to lie about which refusal this is, or about which request it belongs to.
            if (name is not (CodeExtension or "traceId"))
            {
                problem.Extensions[name] = value;
            }
        }

        SetRetryAfter(controller.Response, status, error);

        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }

    /// <summary>
    /// Writes <c>Retry-After</c> on a 429 that named the instant it resolves at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An HTTP-date rather than delta-seconds: an allowance resets at a stored instant in the account's own
    /// calendar, and a relative figure would be a subtraction against the server's clock that the client then
    /// adds back against its own. The absolute form is the one both sides already agree about.
    /// </para>
    /// <para>
    /// Here rather than in each controller, so no route can ship a 429 that tells a client to back off without
    /// saying until when — which would leave it guessing, and guessing wrong is a retry storm.
    /// </para>
    /// </remarks>
    private static void SetRetryAfter(HttpResponse response, int status, OperationError error)
    {
        if (status != StatusCodes.Status429TooManyRequests
            || error.Extensions?.TryGetValue(RetryAtExtension, out var value) is not true
            || value is not DateTimeOffset retryAt)
        {
            return;
        }

        response.Headers.RetryAfter = retryAt.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>Used for requests that fail before reaching a facade, such as unreadable JSON.</summary>
    public static IActionResult MalformedRequest(ActionContext context)
    {
        var factory = context.HttpContext.RequestServices
            .GetRequiredService<Microsoft.AspNetCore.Mvc.Infrastructure.ProblemDetailsFactory>();
        var problem = factory.CreateValidationProblemDetails(
            context.HttpContext, context.ModelState, StatusCodes.Status400BadRequest, title: "The request body could not be read.");
        problem.Extensions[CodeExtension] = MalformedRequestCode;

        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/problem+json" },
        };
    }
}
