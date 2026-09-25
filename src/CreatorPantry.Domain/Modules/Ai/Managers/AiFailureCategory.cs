namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Why an operation reached <see cref="AiOperationStatus.Failed"/>, in terms stable enough to route on.
/// </summary>
/// <remarks>
/// <para>
/// The distinction that matters is retryable versus not. A provider timeout is worth another attempt; output
/// that failed its schema, a domain rule, or a safety check is not, and retrying it blindly just spends
/// tokens arriving at the same answer.
/// </para>
/// <para>
/// The execution wrapper that classifies provider outcomes will extend this. What it may not do is fold two
/// causes into one member because they happened to be handled the same way on the day — a category nobody can
/// route on has stopped being a category.
/// </para>
/// </remarks>
public enum AiFailureCategory
{
    /// <summary>Not declared. Never valid on a stored row; the check constraint refuses it.</summary>
    Unspecified = 0,

    /// <summary>The request was refused before any provider call — a malformed or impossible ask.</summary>
    Validation = 1,

    /// <summary>A workspace or platform limit was already spent.</summary>
    Quota = 2,

    /// <summary>The prompt template could not be resolved, so nothing could be sent.</summary>
    TemplateUnavailable = 3,

    /// <summary>The provider errored or was unreachable. Usually worth another attempt.</summary>
    Provider = 4,

    /// <summary>The provider refused for rate limiting. Retryable, after its stated delay.</summary>
    RateLimited = 5,

    /// <summary>The provider did not answer in time.</summary>
    Timeout = 6,

    /// <summary>The output did not satisfy its declared schema, and guessing at a repair is not allowed.</summary>
    OutputSchemaInvalid = 7,

    /// <summary>The output parsed but broke a domain rule — a quantity that cannot exist, a step out of order.</summary>
    DomainInvalid = 8,

    /// <summary>The provider or a safety check blocked the content. Never retried blindly.</summary>
    SafetyBlocked = 9,

    /// <summary>The creator cancelled it. A failure of the operation, not of anything else.</summary>
    Cancelled = 10,

    /// <summary>
    /// Claimed and abandoned more times than the attempt bound allows  14 a worker died mid-flight on every
    /// attempt.
    /// </summary>
    /// <remarks>
    /// Its own member rather than <see cref="Provider"/>, because the two ask for opposite responses: a
    /// provider outage resolves itself and this does not. Folding them together would make the metric that
    /// would tell an operator their workers are crashlooping read as "the provider is flaky".
    /// </remarks>
    LeaseAbandoned = 11,
}
