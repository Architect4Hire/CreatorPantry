using System.Net;
using Azure;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.AiProvider;

/// <summary>
/// Classification that reads the signals only this provider's SDK carries: the HTTP status on
/// <see cref="RequestFailedException"/>, and its error code for content filtering.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole reason the classifier is an abstraction. A rate limit arrives as
/// <see cref="RequestFailedException"/> with status 429, not as an <see cref="HttpRequestException"/>, so the
/// domain's shape-based default would see an unrecognised exception and treat it as a generic transient fault —
/// losing the rate-limit category and, worse, treating a content-filter block as retryable. The restriction
/// against retrying a nontransient failure depends on getting this right.
/// </para>
/// <para>
/// Falls through to <see cref="DefaultAiFailureClassifier"/> for cancellation, timeout and anything else it
/// does not recognise, so the two are additive rather than alternatives.
/// </para>
/// </remarks>
public sealed class AzureInferenceFailureClassifier : DefaultAiFailureClassifier
{
    /// <summary>
    /// Error codes that mean the content was refused rather than the call having failed.
    /// </summary>
    /// <remarks>
    /// A safety refusal is never retried. It is also not an error in the ordinary sense — the provider did its
    /// job — so it gets its own category rather than being folded into a request failure.
    /// </remarks>
    private static readonly string[] SafetyCodes =
    [
        "content_filter",
        "ResponsibleAIPolicyViolation",
    ];

    public override AiFailureClassification Classify(Exception exception, CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Cancellation first, before anything reads a status: a cancelled call may well be wrapped in a
        // provider exception, and the caller's intent outranks whatever the transport reported.
        if (exception is OperationCanceledException)
        {
            return base.Classify(exception, callerToken);
        }

        return exception switch
        {
            RequestFailedException failed => FromRequestFailed(failed),
            _ => base.Classify(exception, callerToken),
        };
    }

    private static AiFailureClassification FromRequestFailed(RequestFailedException exception)
    {
        if (exception.ErrorCode is { } code
            && SafetyCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
        {
            return new AiFailureClassification(
                AiFailureCategory.SafetyBlocked,
                IsTransient: false,
                RetryAfter: null,
                "The provider's safety filter refused the request or the response.");
        }

        return (HttpStatusCode)exception.Status switch
        {
            HttpStatusCode.TooManyRequests => new(
                AiFailureCategory.RateLimited,
                true,

                // Azure reports the delay in a response header the SDK does not surface on the exception, so
                // the pipeline's own backoff is what paces the retry. Stated rather than silently assumed.
                null,
                "The provider rate-limited the request."),

            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => new(
                AiFailureCategory.Timeout, true, null, "The provider timed out."),

            HttpStatusCode.PaymentRequired or HttpStatusCode.InsufficientStorage => new(
                AiFailureCategory.Quota, false, null, "The provider reports no remaining quota."),

            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(
                AiFailureCategory.Provider, false, null, "The provider rejected the credentials."),

            >= HttpStatusCode.InternalServerError => new(
                AiFailureCategory.Provider, true, null, "The provider returned a server error."),

            { } status when (int)status >= 400 => new(
                AiFailureCategory.Provider,
                false,
                null,
                $"The provider rejected the request ({(int)status})."),

            // Status 0 is the SDK's way of saying the call never reached the service.
            _ => new(AiFailureCategory.Provider, true, null, "The provider could not be reached."),
        };
    }
}
