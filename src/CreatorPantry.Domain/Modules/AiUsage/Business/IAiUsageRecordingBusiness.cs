using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage.Data;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;

namespace CreatorPantry.Domain.Modules.AiUsage.Business;

/// <summary>Turns attributed attempts into ledger rows, and decides which of them are new.</summary>
internal interface IAiUsageRecordingBusiness
{
    /// <inheritdoc cref="Facade.IAiUsageRecordingFacade.StageAttemptsAsync"/>
    Task<int> StageAttemptsAsync(
        AiUsageAttributionServiceModel attribution,
        IReadOnlyCollection<AiUsageAttemptServiceModel> attempts,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiUsageRecordingBusiness"/>
internal sealed class AiUsageRecordingBusiness(IAiUsageDataLayer dataLayer) : IAiUsageRecordingBusiness
{
    public async Task<int> StageAttemptsAsync(
        AiUsageAttributionServiceModel attribution,
        IReadOnlyCollection<AiUsageAttemptServiceModel> attempts,
        CancellationToken cancellationToken)
    {
        // Refused rather than stored: an entry attributed to nobody is a row the ledger can never answer a
        // question about, and it would still count toward a period. The caller resolves this from
        // IWorkspaceContext, so an empty value means the resolution path is broken, not that this attempt is
        // unattributable.
        ArgumentException.ThrowIfNullOrWhiteSpace(attribution.AccountId);

        if (attribution.TaskType is AiTaskType.Unspecified)
        {
            throw new ArgumentException(
                "An attributed attempt must name the task it ran.", nameof(attribution));
        }

        if (attempts.Count == 0)
        {
            return 0;
        }

        // "We do not know what this cost" is a recordable outcome; "we did not say" is not. Caught here so the
        // caller sees which attempt was wrong, rather than a check-constraint violation at the caller's
        // SaveChanges that would roll back the attempt record too.
        foreach (var attempt in attempts.Where(attempt => attempt.Outcome is AiUsageOutcome.Unspecified))
        {
            throw new ArgumentException(
                $"Attempt {attempt.AttemptNumber} did not declare how it ended.", nameof(attempts));
        }

        var alreadyPosted = await dataLayer.FindPostedAttemptNumbersAsync(
            attribution.AiOperationId, cancellationToken);

        var entries = attempts
            .Where(attempt => !alreadyPosted.Contains(attempt.AttemptNumber))
            .Select(attempt => ToEntry(attribution, attempt))
            .ToList();

        if (entries.Count == 0)
        {
            return 0;
        }

        dataLayer.StageEntries(entries);

        return entries.Count;
    }

    private static AccountAiUsageEntry ToEntry(
        AiUsageAttributionServiceModel attribution, AiUsageAttemptServiceModel attempt) =>
        new()
        {
            Id = Guid.NewGuid(),
            AccountId = attribution.AccountId,
            OccurredAt = attempt.OccurredAt,
            AiOperationId = attribution.AiOperationId,
            AttemptNumber = attempt.AttemptNumber,
            TaskType = attribution.TaskType,
            WorkspaceId = attribution.WorkspaceId,
            ProviderName = attempt.ProviderName,
            ModelName = attempt.ModelName,
            ModelDeployment = attempt.ModelDeployment,
            InputTokens = attempt.InputTokens,
            OutputTokens = attempt.OutputTokens,
            TotalTokens = attempt.TotalTokens,
            EstimatedCost = attempt.EstimatedCost,
            IsBillable = attempt.IsBillable,

            // Derived here rather than taken from the caller, so the flag and the columns it describes cannot
            // be handed in disagreeing with each other — the check constraint refuses that pairing anyway, and
            // a caller would only be able to discover it at SaveChanges.
            UsageReported = attempt.InputTokens is not null
                || attempt.OutputTokens is not null
                || attempt.TotalTokens is not null,

            Outcome = attempt.Outcome,
        };
}
