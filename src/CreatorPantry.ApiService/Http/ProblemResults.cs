using System.Globalization;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Nutrition.Managers;
using CreatorPantry.Domain.Modules.Planning.Managers;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using CreatorPantry.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.ApiService.Http;

/// <summary>
/// Maps application errors to RFC 9457 ProblemDetails with a stable <c>code</c>, the request's <c>traceId</c>,
/// and <c>retry</c> advice (<see cref="ProblemRetry"/>).
/// </summary>
public static class ProblemResults
{
    public const string CodeExtension = "code";
    public const string MalformedRequestCode = "request.malformed";
    public const string InternalErrorCode = "internal_error";
    public const string NotFoundCode = "not_found";

    /// <summary>
    /// A write lost an optimistic-concurrency check that no business layer caught. Answered by the exception
    /// handler, never by a facade: a safety net for a path that should have refused with its own code.
    /// </summary>
    public const string ConcurrencyConflictCode = "concurrency.stale.conflict";

    /// <summary>
    /// Conflicts whose remedy is a fresh read: the caller composed against a state that has since moved.
    /// </summary>
    /// <remarks>
    /// An explicit list rather than a suffix rule, because names do not divide the 409s cleanly:
    /// <c>brand.guide.version.stale.conflict</c> is about superseded <em>sources</em> and is not cured by a
    /// re-read, while <c>recipes.recipe.conflict</c> is a stale token with no <c>.stale</c> in it.
    /// <c>ProblemCatalogTests</c> fails on any conflict code missing from this list, <see cref="SameRequestCodes"/>,
    /// and its own reviewed list of state conflicts, so a new one cannot ship unclassified.
    /// </remarks>
    public static IReadOnlySet<string> RereadCodes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        // The account's plan chain moved under an ops change (AP.4.4): read it again and decide against what is
        // there now, rather than resending a change that was composed against the old end of the chain.
        AccountPlanAdministrationErrors.Conflict,

        // A grant's balance moved under a claw-back (AP.5.1): nothing was taken, and what is left is read again.
        AccountCreditGrantAdministrationErrors.Conflict,

        // The billing page offered a command its account's subscription no longer fits (AP.6.5): a Checkout for an
        // account now subscribed, a plan change for one with none, or the plan already in force. Reading the page
        // again offers the right button.
        BillingAccountErrors.AlreadySubscribed,
        BillingAccountErrors.SubscriptionRequired,
        BillingAccountErrors.Unchanged,

        ConcurrencyConflictCode,
        AiFeatureErrors.Conflict,
        AiUsageAdministrationErrors.QuotaConflict,
        BrandErrorCodes.Conflict,
        BrandErrorCodes.SourceConflict,
        BrandErrorCodes.SourceExtractionConflict,
        BrandErrorCodes.GuideActivationConflict,
        BrandErrorCodes.SetupSessionConflict,
        BrandErrorCodes.GuideEditSessionConflict,
        ContentErrorCodes.CardStale,
        ContentErrorCodes.CardLinkedAssetConflict,
        ContentErrorCodes.CreativeContextStale,
        ContentErrorCodes.LongFormDraftStale,
        ContentErrorCodes.LongFormRevisionStale,
        ContentErrorCodes.SocialStale,
        ContentErrorCodes.WeeklyThemesConflict,
        MediaErrorCodes.AssetStaleToken,
        NutritionErrorCodes.MappingStale,
        PlanningErrorCodes.WorkTaskStale,
        PublishingErrorCodes.RecordConflict,
        RecipeErrorCodes.RecipeConflict,
        RecipeErrorCodes.TestRunConflict,
        WorkflowErrorCodes.RunConflict,
    };

    /// <summary>
    /// Conflicts where nothing was written and the same request, unchanged, may succeed: a lost insert race or
    /// a commit that did not land.
    /// </summary>
    public static IReadOnlySet<string> SameRequestCodes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        BrandErrorCodes.GuideVersionApprovalConflict,
        ContentErrorCodes.CardNotSaved,
        ContentErrorCodes.CreativeContextNotSaved,
        ContentErrorCodes.PromptConflict,
        MediaErrorCodes.AssetNotCreated,
        MediaErrorCodes.AssetVersionTaken,
        PlanningErrorCodes.TopThreeNotSaved,
        PlanningErrorCodes.WorkTaskNotSaved,
        PublishingErrorCodes.ScheduleConflict,
        RecipeErrorCodes.TestAttachmentConflict,
    };

    /// <summary>
    /// The <see cref="ProblemRetry"/> advice for a problem: the code's own classification first, then what its
    /// status implies.
    /// </summary>
    public static string RetryFor(string code, int status) =>
        RereadCodes.Contains(code) ? ProblemRetry.Reread
        : SameRequestCodes.Contains(code) ? ProblemRetry.Same
        : ProblemRetry.ForStatus(status);

    /// <summary>
    /// The status for an exception nothing below the controller handled. Everything unrecognised is a 500 with
    /// no detail.
    /// </summary>
    /// <remarks>
    /// A <see cref="BadHttpRequestException"/> is the request's fault (a body that outgrew its limit while
    /// streaming, an unreadable form) and keeps its own status, as it already does at the gateway. A
    /// <see cref="DbUpdateConcurrencyException"/> means the row moved under a write whose business layer did not
    /// catch it; it is a 409 the caller can recover from by reading again, not a fault of ours to hide.
    /// </remarks>
    public static int StatusForException(Exception exception) => exception switch
    {
        BadHttpRequestException badRequest => badRequest.StatusCode,
        DbUpdateConcurrencyException => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>
    /// Completes a problem the framework wrote (exceptions, status-code pages): a stable code and retry advice.
    /// Problems built by <see cref="ProblemFor"/> already carry both and are left as they are.
    /// </summary>
    public static void Customize(ProblemDetailsContext context)
    {
        var status = context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode;
        var code = context.Exception is DbUpdateConcurrencyException ? ConcurrencyConflictCode : CodeForStatus(status);

        context.ProblemDetails.Extensions.TryAdd(CodeExtension, code);
        context.ProblemDetails.Extensions.TryAdd(
            ProblemRetry.Extension,
            RetryFor(context.ProblemDetails.Extensions[CodeExtension] as string ?? code, status));
    }

    /// <summary>The stable code for a framework-generated problem with no application error code.</summary>
    public static string CodeForStatus(int status) => CreatorPantry.ServiceDefaults.EdgeHardening.CodeForStatus(status);

    /// <summary>
    /// The extension a 429 or 503 reads its <c>Retry-After</c> from: an instant, as a <see cref="DateTimeOffset"/>.
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
    /// <c>.payload_too_large</c> 413, <c>.unavailable</c> 503, <c>.suspended</c> 403, <c>.exhausted</c> 429, <c>.render.failed</c> 500 (our own fault, never the caller's). Anything else is a 400 request error.
    /// </summary>
    public static int StatusFor(string code) => code switch
    {
        // The request is well formed, but the key already belongs to a different request.
        IdempotencyPolicy.KeyReusedCode => StatusCodes.Status422UnprocessableEntity,
        // The setup-session codes are underscore-shaped by contract (11A.22), so the suffix rules below cannot
        // reach them.
        CreatorPantry.Domain.Modules.Brand.Managers.BrandErrorCodes.SetupSessionConflict
            or CreatorPantry.Domain.Modules.Brand.Managers.BrandErrorCodes.SetupSessionCompleted
            or CreatorPantry.Domain.Modules.Brand.Managers.BrandErrorCodes.SetupSessionNotFinished => StatusCodes.Status409Conflict,
        CreatorPantry.Domain.Modules.Brand.Managers.BrandErrorCodes.SetupSessionNotFound => StatusCodes.Status404NotFound,
        CreatorPantry.Domain.Modules.Brand.Managers.BrandErrorCodes.SetupSessionForbidden => StatusCodes.Status403Forbidden,
        _ when code.EndsWith(".not_found", StringComparison.Ordinal) => StatusCodes.Status404NotFound,
        _ when code.EndsWith(".forbidden", StringComparison.Ordinal) => StatusCodes.Status403Forbidden,
        _ when code.EndsWith(".conflict", StringComparison.Ordinal) => StatusCodes.Status409Conflict,
        _ when code.EndsWith(".gone", StringComparison.Ordinal) => StatusCodes.Status410Gone,
        _ when code.EndsWith(".unprocessable", StringComparison.Ordinal) => StatusCodes.Status422UnprocessableEntity,

        // An upload that fits the route's body limit but not its own format's. The edge's `request_too_large`
        // shares the status; the code separates them. Deliberately not `.too_large`, which a shipped
        // code already ends in and answers 400 for.
        _ when code.EndsWith(".payload_too_large", StringComparison.Ordinal) => StatusCodes.Status413PayloadTooLarge,

        // A dependency this request needs is not answering. Nothing was written, and the same request may
        // succeed later.
        _ when code.EndsWith(".unavailable", StringComparison.Ordinal) => StatusCodes.Status503ServiceUnavailable,

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

    public static ObjectResult ProblemFor(this ControllerBase controller, OperationError error) =>
        ProblemFor(controller.HttpContext, controller.ProblemDetailsFactory, error);

    /// <summary>
    /// The same problem for a caller that has no controller yet — a resource filter refusing before the body is
    /// read (15.3) — built by the same factory, so the two cannot differ.
    /// </summary>
    public static ObjectResult ProblemFor(HttpContext httpContext, OperationError error) =>
        ProblemFor(httpContext, httpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>(), error);

    private static ObjectResult ProblemFor(HttpContext httpContext, ProblemDetailsFactory factory, OperationError error)
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
        var problem = factory.CreateValidationProblemDetails(httpContext, modelState, status, title: error.Message);
        problem.Extensions[CodeExtension] = error.Code;
        problem.Extensions[ProblemRetry.Extension] = RetryFor(error.Code, status);

        foreach (var (name, value) in error.Extensions ?? new Dictionary<string, object?>())
        {
            // `code`, `retry` and `traceId` are this layer's to set. An application error that could overwrite
            // one would be able to lie about which refusal this is, how to recover from it, or which request it
            // belongs to.
            if (name is not (CodeExtension or ProblemRetry.Extension or "traceId"))
            {
                problem.Extensions[name] = value;
            }
        }

        SetRetryAfter(httpContext.Response, status, error);

        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }

    /// <summary>
    /// Writes <c>Retry-After</c> on a 429 or 503 that named the instant it resolves at.
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
    /// <para>
    /// A 503 gets the header only when its dependency named an instant. Most cannot, and an invented figure
    /// would be worse than none: a client is told <c>retry: later</c> either way.
    /// </para>
    /// </remarks>
    private static void SetRetryAfter(HttpResponse response, int status, OperationError error)
    {
        if (status is not (StatusCodes.Status429TooManyRequests or StatusCodes.Status503ServiceUnavailable)
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
        problem.Extensions[ProblemRetry.Extension] = ProblemRetry.None;

        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/problem+json" },
        };
    }
}
