using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Publishing.Data.Entities;
using CreatorPantry.Domain.Modules.Publishing.Gateways;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Publishing.Data;

/// <summary>One stored profile, as the list reads it. No provider id.</summary>
public sealed record PublishingProfileRecord(
    Guid Id,
    string ProviderKey,
    PublishingPlatform Platform,
    string DisplayName,
    string? TimeZoneId,
    PublishingProfileStatus Status,
    DateTimeOffset LastSeenAt);

/// <summary>When a provider was last asked, and how it went.</summary>
public sealed record PublishingSyncRecord(
    DateTimeOffset LastAttemptAt,
    DateTimeOffset? LastSucceededAt,
    PublishingConnectionStatus LastOutcome,
    DateTimeOffset? RetryNotBefore);

/// <summary>One profile to store, already judged by Business: a usable name and a zone this server knows.</summary>
public sealed record PublishingProfileToStore(
    string ExternalId,
    string ExternalAccountId,
    PublishingPlatform Platform,
    string DisplayName,
    string? TimeZoneId,
    PublishingProfileStatus Status);

/// <summary>EF access for publishing profiles, their provider mappings and the sync record.</summary>
public interface IPublishingProfileRepository
{
    /// <summary>The resolved workspace's profiles that the provider still lists, by network and then name.</summary>
    Task<IReadOnlyList<PublishingProfileRecord>> ListAsync(CancellationToken cancellationToken);

    Task<PublishingSyncRecord?> FindSyncAsync(string providerKey, CancellationToken cancellationToken);

    /// <summary>Tracked, for the write that follows a read of the provider.</summary>
    Task<PublishingProviderSync?> FindSyncForUpdateAsync(string providerKey, CancellationToken cancellationToken);

    /// <summary>Every profile of one provider with its mapping, tracked, keyed by the provider's id.</summary>
    Task<IReadOnlyDictionary<string, PublishingProfile>> FindByExternalIdAsync(string providerKey, CancellationToken cancellationToken);

    void Add(PublishingProfile profile, ExternalPublishingProfileReference reference);

    void Add(PublishingProviderSync sync);
}

internal sealed class PublishingProfileRepository(CreatorPantryDbContext context) : IPublishingProfileRepository
{
    public async Task<IReadOnlyList<PublishingProfileRecord>> ListAsync(CancellationToken cancellationToken) =>
        await context.PublishingProfiles.AsNoTracking()
            .Where(profile => profile.Status != PublishingProfileStatus.Removed)
            .OrderBy(profile => profile.Platform)
            .ThenBy(profile => profile.DisplayName)
            .ThenBy(profile => profile.Id)
            .Select(profile => new PublishingProfileRecord(
                profile.Id,
                profile.ProviderKey,
                profile.Platform,
                profile.DisplayName,
                profile.TimeZoneId,
                profile.Status,
                profile.LastSeenAt))
            .ToListAsync(cancellationToken);

    public Task<PublishingSyncRecord?> FindSyncAsync(string providerKey, CancellationToken cancellationToken) =>
        context.PublishingProviderSyncs.AsNoTracking()
            .Where(sync => sync.ProviderKey == providerKey)
            .Select(sync => new PublishingSyncRecord(sync.LastAttemptAt, sync.LastSucceededAt, sync.LastOutcome, sync.RetryNotBefore))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<PublishingProviderSync?> FindSyncForUpdateAsync(string providerKey, CancellationToken cancellationToken) =>
        context.PublishingProviderSyncs.FirstOrDefaultAsync(sync => sync.ProviderKey == providerKey, cancellationToken);

    public async Task<IReadOnlyDictionary<string, PublishingProfile>> FindByExternalIdAsync(
        string providerKey, CancellationToken cancellationToken)
    {
        var rows = await context.ExternalPublishingProfileReferences
            .Where(reference => reference.ProviderKey == providerKey)
            .Join(
                context.PublishingProfiles,
                reference => reference.PublishingProfileId,
                profile => profile.Id,
                (reference, profile) => new { reference.ExternalId, Profile = profile })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(row => row.ExternalId, row => row.Profile, StringComparer.Ordinal);
    }

    public void Add(PublishingProfile profile, ExternalPublishingProfileReference reference)
    {
        context.PublishingProfiles.Add(profile);
        context.ExternalPublishingProfileReferences.Add(reference);
    }

    public void Add(PublishingProviderSync sync) => context.PublishingProviderSyncs.Add(sync);
}

/// <summary>The publishing profiles' complete persistence operations, and the one provider read behind them.</summary>
public interface IPublishingProfileDataLayer
{
    /// <inheritdoc cref="IPublishingProfileGateway.Describe"/>
    PublishingProviderDescriptor DescribeProvider();

    /// <inheritdoc cref="IPublishingProfileGateway.FetchAsync"/>
    Task<PublishingProfileFetch> FetchFromProviderAsync(CancellationToken cancellationToken);

    /// <inheritdoc cref="IPublishingProfileRepository.ListAsync"/>
    Task<IReadOnlyList<PublishingProfileRecord>> ListAsync(CancellationToken cancellationToken);

    /// <inheritdoc cref="IPublishingProfileRepository.FindSyncAsync"/>
    Task<PublishingSyncRecord?> FindSyncAsync(string providerKey, CancellationToken cancellationToken);

    /// <summary>
    /// Records how asking the provider went and, when it answered, makes storage match what it said — in one
    /// unit of work: all of it, or none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>With profiles</strong>, each is matched to its stored row by the provider's id: a known one is
    /// brought up to date, a new one is stored with its mapping, and one the provider no longer lists is
    /// marked <see cref="PublishingProfileStatus.Removed"/> — never deleted. <strong>Without</strong> — the
    /// read failed — no profile is touched: a provider that could not be reached has not said that anything
    /// was removed.
    /// </para>
    /// <para>
    /// False means nothing was written: another request recorded the same read first, which the unique
    /// indexes and the sync row's version decide. The caller reads what that request stored.
    /// </para>
    /// </remarks>
    /// <param name="profiles">What the provider listed, or null when it did not answer with a list.</param>
    /// <param name="complete">
    /// False when the list was cut short. Known profiles are still brought up to date and new ones stored, but
    /// nothing is marked removed: a profile the read never reached has not been said to be gone.
    /// </param>
    Task<bool> RecordAsync(
        string providerKey,
        PublishingSyncRecord sync,
        IReadOnlyList<PublishingProfileToStore>? profiles,
        bool complete,
        CancellationToken cancellationToken);
}

internal sealed class PublishingProfileDataLayer(
    IPublishingProfileRepository repository,
    IPublishingProfileGateway gateway,
    CreatorPantryDbContext db,
    ILogger<PublishingProfileDataLayer> logger) : IPublishingProfileDataLayer
{
    public PublishingProviderDescriptor DescribeProvider() => gateway.Describe();

    public Task<PublishingProfileFetch> FetchFromProviderAsync(CancellationToken cancellationToken) =>
        gateway.FetchAsync(cancellationToken);

    public Task<IReadOnlyList<PublishingProfileRecord>> ListAsync(CancellationToken cancellationToken) =>
        repository.ListAsync(cancellationToken);

    public Task<PublishingSyncRecord?> FindSyncAsync(string providerKey, CancellationToken cancellationToken) =>
        repository.FindSyncAsync(providerKey, cancellationToken);

    public async Task<bool> RecordAsync(
        string providerKey,
        PublishingSyncRecord sync,
        IReadOnlyList<PublishingProfileToStore>? profiles,
        bool complete,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentNullException.ThrowIfNull(sync);

        var row = await repository.FindSyncForUpdateAsync(providerKey, cancellationToken);
        if (row is null)
        {
            row = new PublishingProviderSync { Id = Guid.NewGuid(), ProviderKey = providerKey };
            repository.Add(row);
        }

        row.LastAttemptAt = sync.LastAttemptAt;
        row.LastSucceededAt = sync.LastSucceededAt;
        row.LastOutcome = sync.LastOutcome;
        row.RetryNotBefore = sync.RetryNotBefore;

        if (profiles is not null)
        {
            var stored = await repository.FindByExternalIdAsync(providerKey, cancellationToken);
            var listed = new HashSet<string>(StringComparer.Ordinal);

            foreach (var profile in profiles)
            {
                // A provider that lists one profile twice has still listed one profile.
                if (!listed.Add(profile.ExternalId))
                {
                    continue;
                }

                if (stored.TryGetValue(profile.ExternalId, out var known))
                {
                    known.Platform = profile.Platform;
                    known.DisplayName = profile.DisplayName;
                    known.TimeZoneId = profile.TimeZoneId;
                    known.Status = profile.Status;
                    known.LastSeenAt = sync.LastAttemptAt;
                    continue;
                }

                var id = Guid.NewGuid();
                repository.Add(
                    new PublishingProfile
                    {
                        Id = id,
                        ProviderKey = providerKey,
                        Platform = profile.Platform,
                        DisplayName = profile.DisplayName,
                        TimeZoneId = profile.TimeZoneId,
                        Status = profile.Status,
                        FirstSeenAt = sync.LastAttemptAt,
                        LastSeenAt = sync.LastAttemptAt,
                    },
                    new ExternalPublishingProfileReference
                    {
                        Id = Guid.NewGuid(),
                        PublishingProfileId = id,
                        ProviderKey = providerKey,
                        ExternalId = profile.ExternalId,
                        ExternalAccountId = profile.ExternalAccountId,
                    });
            }

            foreach (var (externalId, known) in stored)
            {
                if (complete && !listed.Contains(externalId))
                {
                    known.Status = PublishingProfileStatus.Removed;
                }
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateException exception)
        {
            // Almost always another request recording the same read first, which is harmless: the caller reads
            // what that one stored. It could also be a row the store refused, and that must not pass unseen —
            // so it is recorded, by type alone: an inner message can quote a provider's id back.
            logger.LogWarning(
                "A publishing profile read for provider {ProviderKey} was not recorded ({ExceptionType}).",
                providerKey,
                exception.InnerException?.GetType().Name ?? exception.GetType().Name);

            // Nothing of this one is left staged.
            db.ChangeTracker.Clear();

            return false;
        }
    }
}
