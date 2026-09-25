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
}
