using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Gateways;

/// <summary>One request for a completion, already assembled and already bounded.</summary>
/// <param name="Envelope">The built prompt. The gateway sends it and never rewrites it.</param>
/// <param name="ExpectedSchemaVersion">The output schema version the answer is held to.</param>
/// <param name="Scope">The operation's scope, which bounds what a change may address.</param>
/// <param name="PromptTemplateId">Recorded per attempt, for provenance.</param>
/// <param name="PromptTemplateVersion">Recorded per attempt, for provenance.</param>
/// <param name="CorrelationId">Ties every attempt to the request or job that caused it.</param>
public sealed record AiCompletionRequest(
    PromptEnvelope Envelope,
    string ExpectedSchemaVersion,
    AiOperationScope Scope,
    string PromptTemplateId,
    string PromptTemplateVersion,
    Guid CorrelationId);

/// <summary>
/// What one provider attempt cost and how it ended. Shaped to <c>AiExecutionMetadata</c>, which is where the
/// caller persists it.
/// </summary>
/// <remarks>
/// <strong>No content, by construction.</strong> There is no field here for a prompt, a rendered envelope, or a
/// model response — ai.md forbids logging those by default, and a record with nowhere to put them cannot
/// accumulate them. <see cref="FailureSummary"/> is a short sanitized reason, never a payload.
/// </remarks>
public sealed record AiAttemptRecord
{
    public required int AttemptNumber { get; init; }

    public required string ProviderName { get; init; }

    public required string ModelName { get; init; }

    public string? ModelDeployment { get; init; }

    public required string PromptTemplateId { get; init; }

    public required string PromptTemplateVersion { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public required int LatencyMilliseconds { get; init; }

    /// <summary>Null when the provider did not report usage, which is not the same as zero.</summary>
    public int? InputTokens { get; init; }

    /// <inheritdoc cref="InputTokens"/>
    public int? OutputTokens { get; init; }

    /// <summary>An estimate, never an invoice. Null when no price is configured for this model.</summary>
    public decimal? EstimatedCost { get; init; }

    public bool SafetyBlocked { get; init; }

    /// <summary>Null when the attempt succeeded.</summary>
    public AiFailureCategory? FailureCategory { get; init; }

    /// <summary>A short sanitized reason. Never a provider payload or a transcript.</summary>
    public string? FailureSummary { get; init; }

    public required Guid CorrelationId { get; init; }

    /// <summary>
    /// Whether this attempt was a correction of the previous one rather than a fresh ask.
    /// </summary>
    /// <remarks>
    /// Provenance the proposal would otherwise misstate. A corrected answer came from the template <em>plus</em>
    /// a message naming what was wrong with the first reply, so a record claiming the template alone produced
    /// it would be untrue.
    /// </remarks>
    public bool WasSchemaCorrection { get; init; }
}

/// <summary>
/// The result of a completion: a validated document, or the failure that stopped it — plus one
/// <see cref="AiAttemptRecord"/> per provider call made along the way.
/// </summary>
public sealed record AiCompletionOutcome
{
    public AiOutputDocument? Document { get; init; }

    public AiOutputFailure? Failure { get; init; }

    /// <summary>Every attempt, in order. Never empty: a call that reached no provider is not an outcome.</summary>
    public required IReadOnlyList<AiAttemptRecord> Attempts { get; init; }

    public bool Succeeded => Failure is null && Document is not null;
}
