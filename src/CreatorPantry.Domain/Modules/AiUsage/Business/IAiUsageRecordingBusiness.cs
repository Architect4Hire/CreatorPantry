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

    /// <summary>
    /// Stages one ledger entry per variant an image generation was billed for (AP.2.5), skipping charges
    /// already posted. Returns how many were staged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The account is a separate argument because Media never has one.</strong> It is read from the
    /// reservation by the quota seam that calls this, which is the only place it is known after the request
    /// scope has gone.
    /// </para>
    /// <para>
    /// Charges are numbered 1..<c>BilledVariants</c> and the staged ones come first, so a resumed generation's
    /// later charges take the ordinals after the ones already posted rather than colliding with them.
    /// </para>
    /// </remarks>
    Task<int> StageImageChargesAsync(
        string accountId, AiImageUsageServiceModel usage, CancellationToken cancellationToken);
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

    public async Task<int> StageImageChargesAsync(
        string accountId, AiImageUsageServiceModel usage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(usage.ProviderName);
        ArgumentException.ThrowIfNullOrWhiteSpace(usage.ModelName);

        if (usage.StagedVariants < 0 || usage.UnusableVariants < 0 || usage.FlaggedVariants < 0)
        {
            throw new ArgumentException("A variant count cannot be negative.", nameof(usage));
        }

        if (usage.FlaggedVariants > usage.UnusableVariants)
        {
            throw new ArgumentException(
                "A flagged variant is one the provider generated and nothing could be staged from, so there "
                    + "cannot be more of them than there are unusable variants.",
                nameof(usage));
        }

        if (usage.BilledVariants == 0)
        {
            return 0;
        }

        var alreadyPosted = await dataLayer.FindPostedImageChargeNumbersAsync(
            usage.GeneratedImageOperationId, cancellationToken);

        var entries = Enumerable.Range(1, usage.BilledVariants)
            .Where(charge => !alreadyPosted.Contains(charge))
            .Select(charge => ToImageEntry(accountId, usage, charge))
            .ToList();

        if (entries.Count == 0)
        {
            return 0;
        }

        dataLayer.StageEntries(entries);

        return entries.Count;
    }

    /// <summary>
    /// One charge for one picture.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Every token column is null and <c>UsageReported</c> is false, which is the truth rather than a
    /// gap.</strong> An image provider reports no tokens and has none to report; the row's cost is the rate it
    /// was charged at, which lives on the reservation. The check constraint pairing the flag with the counts is
    /// satisfied by all three being absent.
    /// </para>
    /// <para>
    /// <strong><c>IsBillable</c> is always true here</strong>, because this method is only given variants the
    /// provider generated. A variant whose call failed before generation is released by the settlement and
    /// never reaches the ledger at all — there is nothing to record about a picture that was never made.
    /// </para>
    /// </remarks>
    private static AccountAiUsageEntry ToImageEntry(
        string accountId, AiImageUsageServiceModel usage, int charge) =>
        new()
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            OccurredAt = usage.OccurredAt,
            GeneratedImageOperationId = usage.GeneratedImageOperationId,
            AttemptNumber = charge,
            TaskType = AiTaskType.ImageGeneration,
            WorkspaceId = usage.WorkspaceId,
            ProviderName = usage.ProviderName,
            ModelName = usage.ModelName,
            ModelDeployment = usage.ModelDeployment,
            IsBillable = true,
            UsageReported = false,

            // The staged ones take the first ordinals, then the ones the provider withheld, then the rest of
            // what it generated and nothing could be made of. Ordered so a resumed generation's new charges
            // never re-describe a charge already in the ledger under the same ordinal.
            Outcome = charge <= usage.StagedVariants
                ? AiUsageOutcome.Succeeded
                : charge <= usage.StagedVariants + usage.FlaggedVariants
                    ? AiUsageOutcome.SafetyBlocked
                    : AiUsageOutcome.Failed,
        };

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
