using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Data.Entities;

/// <summary>
/// What one call to a provider cost and how it ended. One row per attempt.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Hangs off the operation, not the proposal.</strong> A failed attempt produces a row here and no
/// proposal at all, and lease recovery can make an operation run more than once — so a proposal-owned record
/// would lose precisely the attempts worth diagnosing. The cost of that choice is that "what did this
/// operation cost?" is a sum rather than a column read, which is the right way round: a retry's own latency
/// and failure are recoverable, and a total is always derivable from parts.
/// </para>
/// <para>
/// <strong>This is the diagnostic record, and it holds no content.</strong> No prompt body, no rendered
/// template, no model response, no provider payload — ai.md forbids logging those by default, and there is no
/// column here they could occupy. <see cref="FailureSummary"/> is the single free-text field, deliberately
/// short, for a sanitized reason rather than a transcript.
/// </para>
/// <para>
/// Immutable. An attempt finished the way it finished.
/// </para>
/// </remarks>
public class AiExecutionMetadata : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid AiOperationId { get; set; }

    /// <summary>Sequential from 1 within one operation, and unique there.</summary>
    public int AttemptNumber { get; set; }

    public string ProviderName { get; set; } = string.Empty;

    public string ModelName { get; set; } = string.Empty;

    public string? ModelDeployment { get; set; }

    /// <summary>The prompt template this attempt used, recorded per attempt because a retry may not reuse it.</summary>
    public string PromptTemplateId { get; set; } = string.Empty;

    /// <inheritdoc cref="AiProposal.PromptTemplateVersion"/>
    public string PromptTemplateVersion { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset CompletedAt { get; set; }

    /// <summary>Measured around the provider call, so it can be compared across attempts and models.</summary>
    public int LatencyMilliseconds { get; set; }

    /// <summary>Null when the provider did not report usage, which is not the same as zero.</summary>
    public int? InputTokens { get; set; }

    /// <inheritdoc cref="InputTokens"/>
    public int? OutputTokens { get; set; }

    /// <summary>
    /// What this attempt is believed to have cost, in the workspace's accounting terms.
    /// </summary>
    /// <remarks>
    /// An estimate, and named nowhere as anything else: it is derived from token counts and a price that is
    /// not authoritative here. Nothing bills from this column.
    /// </remarks>
    public decimal? EstimatedCost { get; set; }

    /// <summary>Whether the provider or a safety check blocked or altered the response.</summary>
    public bool SafetyBlocked { get; set; }

    /// <summary>
    /// How the attempt ended, or null when it succeeded. Uses the same categories the operation records, so a
    /// retry decision reads one vocabulary.
    /// </summary>
    public AiFailureCategory? FailureCategory { get; set; }

    /// <summary>A short, sanitized reason. Never a provider payload or a transcript.</summary>
    public string? FailureSummary { get; set; }

    /// <summary>Ties this attempt to the request or job that produced it, across gateway, API and worker.</summary>
    public Guid CorrelationId { get; set; }
}
