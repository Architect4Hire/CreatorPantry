using CreatorPantry.Domain.Idempotency;
using CreatorPantry.Domain.Models.Idempotency;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.ApiService.Http;

/// <summary>
/// HTTP mapping for idempotent commands. Controllers bind the key with
/// <c>[FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey</c> and pass it to the facade.
/// </summary>
public static class IdempotencyHttp
{
    /// <summary>
    /// A replay returns the original success response plus <c>Idempotent-Replayed: true</c>; failures map to
    /// ProblemDetails (a reused key with a different payload is 422).
    /// </summary>
    public static IActionResult IdempotentResult<T>(
        this ControllerBase controller, IdempotentOutcome<T> outcome, Func<T, IActionResult> onSuccess)
    {
        if (outcome.Replayed)
        {
            controller.Response.Headers[IdempotencyPolicy.ReplayedHeader] = "true";
        }

        return outcome.Result.Succeeded ? onSuccess(outcome.Result.Value!) : controller.ProblemFor(outcome.Result.Error!);
    }
}
