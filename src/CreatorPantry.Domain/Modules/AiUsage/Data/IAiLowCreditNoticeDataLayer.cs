using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.AiUsage.Data;

/// <summary>
/// The two writes behind the low-credit notice (B-59): dismissing this period's, and turning the notice off
/// for the account.
/// </summary>
/// <remarks>
/// Both are idempotent, and both take the account as a parameter from a validated session — there is no route
/// segment, body field, query value or header anywhere in this feature that could carry one.
/// </remarks>
internal interface IAiLowCreditNoticeDataLayer
{
    /// <summary>
    /// Dismisses the notice on the period containing <paramref name="now"/>.
    /// </summary>
    /// <returns>
    /// <see cref="AiQuotaWriteOutcome.Applied"/> when this call dismissed it;
    /// <see cref="AiQuotaWriteOutcome.AlreadyDone"/> when it was dismissed already or there is nothing to
    /// dismiss — no period open yet, or one that never crossed the line.
    /// </returns>
    Task<AiQuotaWriteOutcome> DismissAsync(
        string accountId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Records whether this account wants the notice at all, creating its row on first use.</summary>
    Task SetNoticeEnabledAsync(
        string accountId, bool enabled, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiLowCreditNoticeDataLayer"/>
internal sealed class AiLowCreditNoticeDataLayer(
    IAiQuotaRepository quotas, IAiNoticeSettingRepository settings) : IAiLowCreditNoticeDataLayer
{
    /// <summary>What an account with no row of its own gets, and so what needs no row to record.</summary>
    private const bool DefaultLowCreditNoticeEnabled = true;


    /// <remarks>
    /// <strong>Opens no period.</strong> A dismissal is an answer to something the creator is looking at, and
    /// an account with no period open has not been told anything — writing a period's worth of row to record
    /// that it dismissed nothing would be a surprising thing for this to do.
    /// </remarks>
    public async Task<AiQuotaWriteOutcome> DismissAsync(
        string accountId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < AiQuotaPolicy.ContendedWriteAttempts; attempt++)
        {
            var period = await quotas.FindPeriodAtAsync(accountId, now, cancellationToken);

            if (period?.LowCreditNoticedAt is null || period.LowCreditNoticeDismissedAt is not null)
            {
                return AiQuotaWriteOutcome.AlreadyDone;
            }

            period.LowCreditNoticeDismissedAt = now;

            try
            {
                await quotas.SaveAsync(cancellationToken);

                return AiQuotaWriteOutcome.Applied;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Lost to an admission moving the period's counters, not to another dismissal. Re-read and
                // write the dismissal onto the row as it now stands.
                quotas.Forget();
            }
        }

        return AiQuotaWriteOutcome.Contended;
    }

    /// <remarks>
    /// Read, then insert or update, and a lost race on the unique index re-reads and updates instead. Two
    /// tabs turning the notice off at the same moment must not leave an account with two answers to the
    /// question, and the index is what makes that structural rather than hoped for.
    /// </remarks>
    public async Task SetNoticeEnabledAsync(
        string accountId, bool enabled, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < AiQuotaPolicy.ContendedWriteAttempts; attempt++)
        {
            var setting = await settings.FindAsync(accountId, cancellationToken);

            if (setting is null)
            {
                // Already the answer, with no row saying so. Writing one would record a choice that changes
                // nothing and leave a row behind for every account that ever opened the setting.
                if (enabled == DefaultLowCreditNoticeEnabled)
                {
                    return;
                }

                settings.Add(new AccountAiNoticeSetting
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    LowCreditNoticeEnabled = enabled,
                    LastChangedAt = now,
                });
            }
            else
            {
                if (setting.LowCreditNoticeEnabled == enabled)
                {
                    return;
                }

                setting.LowCreditNoticeEnabled = enabled;
                setting.LastChangedAt = now;
            }

            try
            {
                await settings.SaveAsync(cancellationToken);

                return;
            }
            catch (DbUpdateException) when (attempt < AiQuotaPolicy.ContendedWriteAttempts - 1)
            {
                settings.Forget();
            }
        }
    }
}
