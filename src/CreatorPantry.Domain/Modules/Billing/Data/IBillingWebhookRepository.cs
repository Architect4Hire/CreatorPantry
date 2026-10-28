using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Billing.Data;

/// <summary>EF access for provider events and the subscriptions they are about (AP.6.2).</summary>
/// <remarks>
/// No <c>IgnoreQueryFilters()</c>, because there is no filter to ignore: neither record is
/// <see cref="IWorkspaceOwned"/>, and every query here is keyed by the event, the subscription or the account asked
/// about.
/// </remarks>
internal interface IBillingWebhookRepository
{
    /// <summary>Seeks <c>UX_BillingProviderEvents_ExternalEventId</c>.</summary>
    Task<bool> EventExistsAsync(string externalEventId, CancellationToken cancellationToken);

    /// <summary>Untracked.</summary>
    Task<BillingProviderEvent?> FindEventAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Tracked, for the one update an event row takes: its outcome.</summary>
    Task<BillingProviderEvent?> FindEventForUpdateAsync(Guid id, CancellationToken cancellationToken);

    void AddEvent(BillingProviderEvent providerEvent);

    /// <summary>Tracked. Seeks <c>UX_BillingSubscriptions_ExternalSubscriptionId</c>.</summary>
    Task<BillingSubscription?> FindSubscriptionForUpdateAsync(
        string externalSubscriptionId, CancellationToken cancellationToken);

    /// <summary>Untracked. Every subscription of the account in one mode.</summary>
    Task<IReadOnlyList<BillingSubscription>> ListSubscriptionsAsync(
        string accountId, bool isLiveMode, CancellationToken cancellationToken);

    void AddSubscription(BillingSubscription subscription);

    /// <summary>Untracked. The subscription record's row version, or null when there is no record yet.</summary>
    Task<byte[]?> FindSubscriptionVersionAsync(string externalSubscriptionId, CancellationToken cancellationToken);

    /// <summary>
    /// Untracked. Events still <c>Received</c> that arrived before <paramref name="before"/> and were not queued again
    /// since then, oldest first.
    /// </summary>
    Task<IReadOnlyList<BillingProviderEvent>> ListStrandedAsync(
        DateTimeOffset before, int take, CancellationToken cancellationToken);

    /// <summary>Forgets everything tracked, so a rejected write cannot ride along on a later save.</summary>
    void Forget();

    Task SaveAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBillingWebhookRepository"/>
internal sealed class BillingWebhookRepository(CreatorPantryDbContext context) : IBillingWebhookRepository
{
    public Task<bool> EventExistsAsync(string externalEventId, CancellationToken cancellationToken) =>
        context.BillingProviderEvents.AnyAsync(providerEvent => providerEvent.ExternalEventId == externalEventId, cancellationToken);

    public Task<BillingProviderEvent?> FindEventAsync(Guid id, CancellationToken cancellationToken) =>
        context.BillingProviderEvents.AsNoTracking().SingleOrDefaultAsync(providerEvent => providerEvent.Id == id, cancellationToken);

    public Task<BillingProviderEvent?> FindEventForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        context.BillingProviderEvents.SingleOrDefaultAsync(providerEvent => providerEvent.Id == id, cancellationToken);

    public void AddEvent(BillingProviderEvent providerEvent) => context.BillingProviderEvents.Add(providerEvent);

    public Task<BillingSubscription?> FindSubscriptionForUpdateAsync(
        string externalSubscriptionId, CancellationToken cancellationToken) =>
        context.BillingSubscriptions.SingleOrDefaultAsync(
            subscription => subscription.ExternalSubscriptionId == externalSubscriptionId, cancellationToken);

    public async Task<IReadOnlyList<BillingSubscription>> ListSubscriptionsAsync(
        string accountId, bool isLiveMode, CancellationToken cancellationToken) =>
        await context.BillingSubscriptions
            .AsNoTracking()
            .Where(subscription => subscription.AccountId == accountId && subscription.IsLiveMode == isLiveMode)
            .OrderBy(subscription => subscription.ExternalSubscriptionId)
            .ToListAsync(cancellationToken);

    public void AddSubscription(BillingSubscription subscription) => context.BillingSubscriptions.Add(subscription);

    public Task<byte[]?> FindSubscriptionVersionAsync(string externalSubscriptionId, CancellationToken cancellationToken) =>
        context.BillingSubscriptions
            .AsNoTracking()
            .Where(subscription => subscription.ExternalSubscriptionId == externalSubscriptionId)
            .Select(subscription => subscription.RowVersion)
            .SingleOrDefaultAsync(cancellationToken);

    /// <remarks>Seeks <c>IX_BillingProviderEvents_Status_ReceivedAt</c>.</remarks>
    public async Task<IReadOnlyList<BillingProviderEvent>> ListStrandedAsync(
        DateTimeOffset before, int take, CancellationToken cancellationToken) =>
        await context.BillingProviderEvents
            .AsNoTracking()
            .Where(providerEvent => providerEvent.Status == BillingProviderEventStatus.Received
                && providerEvent.ReceivedAt < before
                && (providerEvent.RedrivenAt == null || providerEvent.RedrivenAt < before))
            .OrderBy(providerEvent => providerEvent.ReceivedAt)
            .Take(take)
            .ToListAsync(cancellationToken);

    public void Forget() => context.ChangeTracker.Clear();

    public Task SaveAsync(CancellationToken cancellationToken) => context.SaveChangesAsync(cancellationToken);
}
