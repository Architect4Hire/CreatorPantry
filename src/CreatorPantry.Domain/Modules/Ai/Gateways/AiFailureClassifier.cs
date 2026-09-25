using System.Net;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Gateways;

/// <summary>How one provider failure should be understood and whether it is worth another attempt.</summary>
/// <param name="Category">The stable category recorded against the attempt and the operation.</param>
/// <param name="IsTransient">
/// Whether retrying could plausibly succeed. False for every policy, safety, schema and request failure — the
/// restriction that a nontransient failure is never retried blindly lives here.
/// </param>
/// <param name="RetryAfter">A delay the provider asked for, when it said one.</param>
/// <param name="Summary">A short sanitized reason. Never a payload.</param>
public sealed record AiFailureClassification(
    AiFailureCategory Category,
    bool IsTransient,
    TimeSpan? RetryAfter,
    string Summary);

/// <summary>
/// Turns a provider exception into a category. Implemented per provider, because the interesting signals —
/// a rate-limit status, a content-filter code — live in SDK-specific exception types the domain may not name.
/// </summary>
/// <remarks>
/// The same split as <c>IAccountMessageSender</c>: the domain declares the seam and a provider assembly
/// supplies the adapter. <see cref="DefaultAiFailureClassifier"/> is the conservative fallback for the shapes
/// the domain <em>can</em> name.
/// </remarks>
public interface IAiFailureClassifier
{
    AiFailureClassification Classify(Exception exception, CancellationToken callerToken);
}

/// <summary>
/// Classification from exception shapes the domain is allowed to name: cancellation, timeout, and
/// <see cref="HttpRequestException"/> with its status code.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately conservative, and deliberately not the whole story. A provider whose SDK raises its own
/// exception type — carrying the status code and the content-filter reason on properties this class cannot see
/// — reaches the fallback at the bottom and is treated as a transient provider fault. That is the right default
/// for an unknown transport error and the wrong answer for a safety block, which is exactly why a
/// provider-specific classifier is registered over this one in a deployed host.
/// </para>
/// <para>
/// Ordering matters here. Cancellation is checked before timeout because a per-attempt timeout is implemented
/// as cancellation, and the two are told apart by whether the <em>caller's</em> token is the one that fired.
/// </para>
/// </remarks>
public class DefaultAiFailureClassifier : IAiFailureClassifier
{
    public virtual AiFailureClassification Classify(Exception exception, CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            // The creator cancelled. Their own token fired, so this is not a fault and not retryable.
            OperationCanceledException when callerToken.IsCancellationRequested =>
                new(AiFailureCategory.Cancelled, false, null, "The request was cancelled."),

            // A token fired that was not the caller's: the per-attempt deadline.
            OperationCanceledException or TimeoutException =>
                new(AiFailureCategory.Timeout, true, null, "The provider did not answer within the attempt timeout."),

            HttpRequestException http => FromStatus(http),

            _ => new(
                AiFailureCategory.Provider,
                true,
                null,
                $"The provider call failed ({exception.GetType().Name})."),
        };
    }

    /// <summary>
    /// A status code is the most reliable transience signal available, and the only one whose meaning is
    /// agreed across providers.
    /// </summary>
    protected static AiFailureClassification FromStatus(HttpRequestException exception) =>
        exception.StatusCode switch
        {
            HttpStatusCode.TooManyRequests =>
                new(AiFailureCategory.RateLimited, true, null, "The provider rate-limited the request."),

            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout =>
                new(AiFailureCategory.Timeout, true, null, "The provider timed out."),

            // Quota is exhausted rather than momentarily unavailable; another attempt spends nothing but time.
            HttpStatusCode.PaymentRequired =>
                new(AiFailureCategory.Quota, false, null, "The provider reports no remaining quota."),

            // Authentication and authorization will not fix themselves, and retrying a rejected credential is
            // how an account gets locked.
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new(AiFailureCategory.Provider, false, null, "The provider rejected the credentials."),

            >= HttpStatusCode.InternalServerError =>
                new(AiFailureCategory.Provider, true, null, "The provider returned a server error."),

            // Any other 4xx is a request this code built wrongly. Sending it again changes nothing.
            { } status when (int)status >= 400 =>
                new(AiFailureCategory.Provider, false, null, $"The provider rejected the request ({(int)status})."),

            // No status at all is a transport failure: DNS, TLS, a dropped connection.
            _ => new(AiFailureCategory.Provider, true, null, "The provider could not be reached."),
        };
}

/// <summary>
/// Raised around a provider failure the classifier called transient, so the resilience pipeline can recognise
/// it without knowing anything else about the domain.
/// </summary>
/// <remarks>
/// The host hands <c>AddAiResilience</c> a predicate matching this type. That is what lets the pipeline's
/// thresholds live in ServiceDefaults while the judgement of what is transient stays here.
/// </remarks>
public sealed class AiTransientFailureException(AiFailureClassification classification, Exception inner)
    : Exception(classification.Summary, inner)
{
    public AiFailureClassification Classification { get; } = classification;
}
