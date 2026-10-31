using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Gateways;

/// <summary>What the gateway needs that is deployment configuration rather than per-request.</summary>
/// <remarks>
/// The resilience thresholds are deliberately absent: timeout, backoff and breaker windows belong to the named
/// pipeline in ServiceDefaults, and having them in two places would mean two answers.
/// </remarks>
public sealed class AiGatewayOptions
{
    public const string SectionName = "Ai:Gateway";

    /// <summary>The name of the resilience pipeline to run provider calls inside.</summary>
    /// <remarks>
    /// Supplied by the host from <c>AiResilience.PipelineKey</c>, because the domain may not reference
    /// ServiceDefaults and neither side should hard-code the other's string.
    /// </remarks>
    public string ResiliencePipelineKey { get; set; } = string.Empty;

    /// <summary>Recorded on every attempt, for provenance.</summary>
    public string ProviderName { get; set; } = "unknown";

    /// <summary>
    /// The model recorded when the provider's response does not name one itself.
    /// </summary>
    /// <remarks>
    /// A fallback, not the source of truth. The response's own model id is preferred, because a deployment can
    /// be repointed at a different model without this configuration changing.
    /// </remarks>
    public string ModelName { get; set; } = "unknown";

    public string? ModelDeployment { get; set; }

    /// <summary>
    /// How many times a schema failure may be met with a corrective re-ask. One, and one is the intent.
    /// </summary>
    /// <remarks>
    /// Higher numbers spend a creator's budget converging on an answer a model has already shown it will not
    /// produce. Zero turns the correction off entirely, which is a legitimate setting for a cost-sensitive
    /// deployment.
    /// </remarks>
    public int MaxSchemaCorrections { get; set; } = 1;

    /// <summary>
    /// The most output tokens one call may produce, for a task with no entry in
    /// <see cref="MaxOutputTokensByTask"/> (AP.2.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// It bounds what one call can spend on output: startup refuses a task whose per-call estimate is below this
    /// ceiling's worst-case credits at the chat model's output rate. It is an output bound only — input is not
    /// covered, and a correction re-sends the prompt — so the estimate is a sized hold, not a guarantee of the
    /// charge. On a reasoning model the ceiling covers the model's hidden reasoning as well as its answer.
    /// </para>
    /// <para>
    /// Deployment configuration, never a request field: nothing a creator sends can raise it. An answer the
    /// provider cuts off here is <see cref="AiOutputReason.Truncated"/>, not a success.
    /// </para>
    /// </remarks>
    public int MaxOutputTokens { get; set; } = 4_000;

    /// <summary>
    /// A task's own ceiling where the default does not fit it. Long-form writes the most per call, so it starts
    /// with room for a whole section and the reasoning behind it.
    /// </summary>
    public Dictionary<AiTaskType, int> MaxOutputTokensByTask { get; } = new()
    {
        [AiTaskType.LongFormOutline] = 8_000,
        [AiTaskType.LongFormSection] = 8_000,
    };

    /// <summary>The ceiling a call for <paramref name="task"/> is sent with.</summary>
    public int MaxOutputTokensFor(AiTaskType task) =>
        MaxOutputTokensByTask.TryGetValue(task, out var ceiling) ? ceiling : MaxOutputTokens;
}
