using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.AiUsage.Data;

namespace CreatorPantry.Domain.Modules.AiUsage.Business;

/// <summary>
/// What a creator may do about the low-credit notice: put this period's away, and say whether they want it at
/// all (PRICE-011 as amended, B-59).
/// </summary>
/// <remarks>
/// Raising the notice is not here. Admission raises it, because admission is the only place that knows what a
/// request has just left the account with, and it does so after its own hold has committed.
/// </remarks>
internal interface IAiLowCreditNoticeBusiness
{
    Task DismissAsync(string accountId, CancellationToken cancellationToken);

    Task SetEnabledAsync(string accountId, bool enabled, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiLowCreditNoticeBusiness"/>
internal sealed class AiLowCreditNoticeBusiness(
    IAiLowCreditNoticeDataLayer data, IClock clock) : IAiLowCreditNoticeBusiness
{
    /// <remarks>
    /// Nothing to dismiss is not an error. A creator whose period rolled between the notice arriving on their
    /// screen and their clicking it away has had the answer they wanted either way, and a 404 would only
    /// tell them something about their own allowance they cannot act on.
    /// </remarks>
    public Task DismissAsync(string accountId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        return data.DismissAsync(accountId, clock.UtcNow, cancellationToken);
    }

    /// <remarks>
    /// The choice is the account's and applies to every workspace it works in, for the reason the allowance
    /// itself does (USAGE-003): one person, one allowance, one answer about being told it is running low.
    /// </remarks>
    public Task SetEnabledAsync(string accountId, bool enabled, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        return data.SetNoticeEnabledAsync(accountId, enabled, clock.UtcNow, cancellationToken);
    }
}
