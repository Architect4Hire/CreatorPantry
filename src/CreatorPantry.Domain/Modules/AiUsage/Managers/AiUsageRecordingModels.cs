using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.AiUsage.Managers;

/// <summary>
/// Who and where one batch of attempts is charged to. Every field is resolved server-side by the caller
/// before this type exists.
/// </summary>
/// <param name="AccountId">
/// The Identity account, from <c>IWorkspaceContext.AccountId</c> — never a request field, an unsigned header,
/// or anything a model supplied (USAGE-001).
/// </param>
/// <param name="WorkspaceId">Where the work was done, as a reporting dimension only. Never an owner.</param>
/// <param name="AiOperationId">The operation the attempts belong to.</param>
/// <param name="TaskType">Which capability ran, so consumption can be broken down by task.</param>
public sealed record AiUsageAttributionServiceModel(
    string AccountId,
    Guid WorkspaceId,
    Guid AiOperationId,
    AiTaskType TaskType);

/// <summary>
/// One provider attempt, in the only terms the ledger records.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Counts and outcome, and nothing else.</strong> There is deliberately no failure summary, no prompt
/// template, no latency and no correlation id here, although the caller has all of them: those belong to
/// <c>AiExecutionMetadata</c>, inside the workspace filter. This type is the boundary that keeps the
/// unfiltered ledger free of everything the filtered record already holds.
/// </para>
/// <para>
/// <see cref="TotalTokens"/> is present and will be null until a gateway reports a provider total. It is
/// never computed from the other two — see <c>AccountAiUsageEntry.TotalTokens</c>.
/// </para>
/// </remarks>
public sealed record AiUsageAttemptServiceModel
{
    /// <summary>Sequential from 1 within one operation, matching the execution record's own numbering.</summary>
    public required int AttemptNumber { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required string ProviderName { get; init; }

    public required string ModelName { get; init; }

    public string? ModelDeployment { get; init; }

    /// <summary>Null when the provider did not report usage, which is not the same as zero.</summary>
    public int? InputTokens { get; init; }

    /// <inheritdoc cref="InputTokens"/>
    public int? OutputTokens { get; init; }

    /// <inheritdoc cref="InputTokens"/>
    public int? TotalTokens { get; init; }

    public decimal? EstimatedCost { get; init; }

    public required AiUsageOutcome Outcome { get; init; }

    /// <summary>Whether this attempt counts against the account's allowance.</summary>
    /// <remarks>
    /// Decided by the caller, which knows whether a provider was reached, and recorded rather than inferred at
    /// read time so a later policy change cannot re-price a settled period.
    /// </remarks>
    public required bool IsBillable { get; init; }
}
