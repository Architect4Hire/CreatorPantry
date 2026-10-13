using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Publishing.Data;
using CreatorPantry.Domain.Modules.Publishing.Gateways;
using CreatorPantry.Domain.Modules.Publishing.Managers;

namespace CreatorPantry.Domain.Modules.Publishing.Business;

/// <summary>The rules for reading a workspace's publishing profiles (PUB-001).</summary>
public interface IPublishingProfileBusiness
{
    /// <summary>
    /// The workspace's profiles and where it stands with its provider. Never fails: every way the provider can
    /// let the read down is a <see cref="PublishingConnectionStatus"/>, beside whatever was read before.
    /// </summary>
    /// <param name="refresh">
    /// True to ask the provider now rather than serve a recent read. Still held to
    /// <see cref="PublishingPolicy.MinimumRefreshInterval"/>, and never asked while it is throttling.
    /// </param>
    Task<PublishingProfilesServiceModel> GetAsync(bool refresh, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPublishingProfileBusiness"/>
/// <remarks>
/// <para>
/// <strong>Whose provider it is, is decided here.</strong> A provider credential is one account at the
/// provider, configured together with the single workspace that account belongs to. For any other workspace
/// the provider is not asked at all and the answer is <see cref="PublishingConnectionStatus.NotConfigured"/>
/// — the same answer a host with no provider gives, so one workspace cannot learn that another has one
/// (tenancy.md). The workspace is the resolved context's id, never anything from input, and never a slug: a
/// free slug can be claimed by creating a workspace with the right name, and an id cannot.
/// </para>
/// <para>
/// <strong>The provider is asked sparingly.</strong> Its quota is small and shared with everything else this
/// deployment will do with it, so a read is served from storage while the last good one is fresh, and the
/// provider is left alone for a while after it throttled, refused or could not be reached.
/// </para>
/// <para>
/// <strong>What was read before stays readable.</strong> A provider that is down, throttling or no longer
/// configured changes the connection status and nothing else: the stored profiles are still returned
/// (external.md, publishing.md).
/// </para>
/// </remarks>
internal sealed class PublishingProfileBusiness(
    IPublishingProfileDataLayer dataLayer,
    ITimeZoneConverter zones,
    IWorkspaceContext workspace,
    IClock clock) : IPublishingProfileBusiness
{
    public async Task<PublishingProfilesServiceModel> GetAsync(bool refresh, CancellationToken cancellationToken)
    {
        var provider = dataLayer.DescribeProvider();

        var mine = provider is { Configured: true, ProviderKey: not null, WorkspaceId: { } owner }
            && owner != Guid.Empty
            && owner == workspace.WorkspaceId;

        if (!mine)
        {
            return new PublishingProfilesServiceModel(
                new PublishingConnectionServiceModel(null, PublishingConnectionStatus.NotConfigured, null, null, null),
                await ProfilesAsync(cancellationToken));
        }

        var providerKey = provider.ProviderKey!;
        var now = clock.UtcNow;
        var sync = await dataLayer.FindSyncAsync(providerKey, cancellationToken);

        if (IsDue(sync, refresh, now))
        {
            var fetch = await dataLayer.FetchFromProviderAsync(cancellationToken);
            var next = Next(sync, fetch, now);

            // Lost to another request recording the same read: what that one stored is read below.
            await dataLayer.RecordAsync(
                providerKey,
                next,
                fetch.Outcome is PublishingProfileFetchOutcome.Fetched ? [.. fetch.Profiles.Select(ToStore)] : null,
                complete: !fetch.Truncated,
                cancellationToken);

            sync = await dataLayer.FindSyncAsync(providerKey, cancellationToken) ?? next;
        }

        var wait = sync?.RetryNotBefore is { } notBefore && notBefore > now ? notBefore - now : (TimeSpan?)null;

        return new PublishingProfilesServiceModel(
            new PublishingConnectionServiceModel(
                providerKey,
                sync?.LastOutcome ?? PublishingConnectionStatus.Unavailable,
                sync?.LastAttemptAt,
                sync?.LastSucceededAt,
                wait is { } left ? (int)Math.Ceiling(left.TotalSeconds) : null),
            await ProfilesAsync(cancellationToken));
    }

    /// <summary>Whether the provider should be asked now.</summary>
    private static bool IsDue(PublishingSyncRecord? sync, bool refresh, DateTimeOffset now)
    {
        if (sync is null)
        {
            return true;
        }

        // However it is asked, never more often than this.
        if (now - sync.LastAttemptAt < PublishingPolicy.MinimumRefreshInterval)
        {
            return false;
        }

        if (sync.RetryNotBefore is { } notBefore && now < notBefore)
        {
            // A throttle is the provider's own instruction and a refresh does not override it. The other
            // waits are this server's caution, which a creator asking for a refresh may set aside — after
            // an operator replaced a refused key, say.
            return refresh && sync.LastOutcome is not PublishingConnectionStatus.RateLimited;
        }

        return refresh
            || sync.LastOutcome is not PublishingConnectionStatus.Connected
            || sync.LastSucceededAt is not { } succeeded
            || now - succeeded >= PublishingPolicy.ProfilesFreshFor;
    }

    /// <summary>The sync record after this attempt.</summary>
    private static PublishingSyncRecord Next(PublishingSyncRecord? previous, PublishingProfileFetch fetch, DateTimeOffset now) =>
        fetch.Outcome switch
        {
            PublishingProfileFetchOutcome.Fetched =>
                new PublishingSyncRecord(now, now, PublishingConnectionStatus.Connected, null),

            PublishingProfileFetchOutcome.RateLimited => new PublishingSyncRecord(
                now,
                previous?.LastSucceededAt,
                PublishingConnectionStatus.RateLimited,
                now + Bounded(fetch.RetryAfter ?? PublishingPolicy.RefusedBackoff)),

            PublishingProfileFetchOutcome.CredentialRejected => new PublishingSyncRecord(
                now, previous?.LastSucceededAt, PublishingConnectionStatus.CredentialRejected, now + PublishingPolicy.RefusedBackoff),

            // Unavailable — and NotConfigured, which a gateway that described itself as configured should
            // never answer; if one does, it is a provider that cannot be used just now.
            _ => new PublishingSyncRecord(
                now, previous?.LastSucceededAt, PublishingConnectionStatus.Unavailable, now + PublishingPolicy.UnavailableBackoff),
        };

    /// <summary>A provider's "retry after", believed within limits: at least the minimum gap, at most a day.</summary>
    private static TimeSpan Bounded(TimeSpan retryAfter) =>
        retryAfter < PublishingPolicy.MinimumRefreshInterval ? PublishingPolicy.MinimumRefreshInterval
        : retryAfter > PublishingPolicy.MaximumRetryAfter ? PublishingPolicy.MaximumRetryAfter
        : retryAfter;

    /// <summary>
    /// A provider's profile as it will be stored: a name that fits and is not blank, and a time zone only if
    /// it is one this server can compute with.
    /// </summary>
    private PublishingProfileToStore ToStore(ProviderPublishingProfile profile)
    {
        var name = profile.DisplayName.Trim();
        if (name.Length == 0)
        {
            // The provider gave no name. The network is what is known, and it is said plainly rather than made up.
            name = profile.Platform.ToString();
        }
        else if (name.Length > PublishingPolicy.DisplayNameMaxLength)
        {
            name = name[..PublishingPolicy.DisplayNameMaxLength].TrimEnd();
        }

        var zone = profile.TimeZoneId?.Trim();

        return new PublishingProfileToStore(
            profile.ExternalId,
            profile.ExternalAccountId,
            profile.Platform,
            name,
            zone is { Length: > 0 and <= PublishingPolicy.TimeZoneIdMaxLength } && zones.IsValidZone(zone) ? zone : null,
            profile.Status);
    }

    private async Task<IReadOnlyList<PublishingProfileServiceModel>> ProfilesAsync(CancellationToken cancellationToken) =>
        [.. (await dataLayer.ListAsync(cancellationToken)).Select(profile => new PublishingProfileServiceModel(
            profile.Id,
            profile.ProviderKey,
            profile.Platform,
            profile.DisplayName,
            profile.TimeZoneId,
            profile.Status,
            profile.LastSeenAt))];
}
