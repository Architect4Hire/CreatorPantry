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
    /// HTTP status for an application error code. Codes follow <c>area.reason</c>; the reason suffix selects
    /// the status so new features map consistently: <c>.not_found</c> 404 (also used to hide existence),
    /// <c>.forbidden</c> 403, <c>.conflict</c> 409 (e.g. stale concurrency token), <c>.gone</c> 410. Anything
    /// else is a 400 request error.
    /// </summary>
    public static int StatusFor(string code) => code switch
    {
        // The request is well formed, but the key already belongs to a different request.
        IdempotencyPolicy.KeyReusedCode => StatusCodes.Status422UnprocessableEntity,
        _ when code.EndsWith(".not_found", StringComparison.Ordinal) => StatusCodes.Status404NotFound,
        _ when code.EndsWith(".forbidden", StringComparison.Ordinal) => StatusCodes.Status403Forbidden,
        _ when code.EndsWith(".conflict", StringComparison.Ordinal) => StatusCodes.Status409Conflict,
        _ when code.EndsWith(".gone", StringComparison.Ordinal) => StatusCodes.Status410Gone,
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

        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
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
