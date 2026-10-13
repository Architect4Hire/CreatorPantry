using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Publishing.Business;
using CreatorPantry.Domain.Modules.Publishing.Data;
using CreatorPantry.Domain.Modules.Publishing.Data.Entities;
using CreatorPantry.Domain.Modules.Publishing.Gateways;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CreatorPantry.Tests.Publishing;

/// <summary>
/// PUB-001's rules and storage against a real SQL Server: whose provider it is, how sparingly it is asked, what
/// is stored and what is never returned, and what happens when it lets the read down.
/// </summary>
/// <remarks>
/// The seam below the facade is built by hand over the fixture's own <c>DbContext</c>, with a scripted provider
/// and a clock the test moves. The fixture is shared, so each test uses a provider key of its own and every
/// assertion is scoped to it.
/// </remarks>
public sealed class PublishingProfileSeamTests(SqlServerRecipeFixture fixture) : IClassFixture<SqlServerRecipeFixture>
{
    private static readonly Guid WsA = SqlServerRecipeFixture.WorkspaceA;
    private static readonly Guid WsB = SqlServerRecipeFixture.WorkspaceB;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ProviderPublishingProfile Instagram(string externalId = "ch_EXTERNAL_ig", string name = "Cozy Fall Kitchen") =>
        new(externalId, "org_EXTERNAL_1", PublishingPlatform.Instagram, name, "America/Chicago", PublishingProfileStatus.Ready);

    private static ProviderPublishingProfile Facebook(string externalId = "ch_EXTERNAL_fb") =>
        new(externalId, "org_EXTERNAL_1", PublishingPlatform.Facebook, "Cozy Fall", "Europe/Dublin", PublishingProfileStatus.Ready);

    private static PublishingProfileFetch Listing(params ProviderPublishingProfile[] profiles) =>
        new(PublishingProfileFetchOutcome.Fetched, profiles);

    // ---- The read ----

    [Fact]
    public async Task The_first_read_asks_the_provider_and_stores_each_profile_under_an_id_of_our_own()
    {
        var provider = new ScriptedProvider(WsA, Listing(Instagram(), Facebook()));
        var clock = new MovableClock();

        var result = await ReadAsync(WsA, provider, clock);

        Assert.Equal(1, provider.Fetches);
        Assert.Equal(PublishingConnectionStatus.Connected, result.Connection.Status);
        Assert.Equal(provider.Key, result.Connection.ProviderKey);
        Assert.Equal(clock.UtcNow, result.Connection.LastCheckedAt);
        Assert.Equal(clock.UtcNow, result.Connection.LastSucceededAt);
        Assert.Null(result.Connection.RetryAfterSeconds);

        var mine = Mine(result, provider);
        Assert.Equal(
            [(PublishingPlatform.Instagram, "Cozy Fall Kitchen", "America/Chicago"), (PublishingPlatform.Facebook, "Cozy Fall", "Europe/Dublin")],
            mine.Select(profile => (profile.Platform, profile.DisplayName, profile.TimeZoneId!)));
        Assert.All(mine, profile => Assert.Equal(PublishingProfileStatus.Ready, profile.Status));

        // The provider's ids are in the mapping records, each pointing at the id that was returned.
        var mapped = await InAsync(WsA, db => db.ExternalPublishingProfileReferences.AsNoTracking()
            .Where(reference => reference.ProviderKey == provider.Key)
            .ToDictionaryAsync(reference => reference.ExternalId, reference => reference.PublishingProfileId, Ct));
        Assert.Equal(mine.Single(profile => profile.Platform is PublishingPlatform.Instagram).Id, mapped["ch_EXTERNAL_ig"]);
        Assert.Equal(mine.Single(profile => profile.Platform is PublishingPlatform.Facebook).Id, mapped["ch_EXTERNAL_fb"]);

        // And nowhere in what a caller is given.
        Assert.DoesNotContain("EXTERNAL", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_recent_read_is_served_from_storage_and_the_provider_is_asked_again_once_it_is_stale()
    {
        var provider = new ScriptedProvider(WsA, Listing(Instagram()));
        var clock = new MovableClock();
        var first = await ReadAsync(WsA, provider, clock);

        clock.Advance(PublishingPolicy.ProfilesFreshFor - TimeSpan.FromSeconds(1));
        var served = await ReadAsync(WsA, provider, clock);

        Assert.Equal(1, provider.Fetches);
        Assert.Equal(first.Connection.LastCheckedAt, served.Connection.LastCheckedAt);
        Assert.Equal(Mine(first, provider), Mine(served, provider));

        clock.Advance(TimeSpan.FromSeconds(1));
        var asked = await ReadAsync(WsA, provider, clock);

        Assert.Equal(2, provider.Fetches);
        Assert.Equal(clock.UtcNow, asked.Connection.LastCheckedAt);
    }

    [Fact]
    public async Task A_refresh_asks_now_but_never_more_than_once_a_minute()
    {
        var provider = new ScriptedProvider(WsA, Listing(Instagram()));
        var clock = new MovableClock();
        await ReadAsync(WsA, provider, clock);

        clock.Advance(PublishingPolicy.MinimumRefreshInterval - TimeSpan.FromSeconds(1));
        await ReadAsync(WsA, provider, clock, refresh: true);
        Assert.Equal(1, provider.Fetches);

        clock.Advance(TimeSpan.FromSeconds(1));
        await ReadAsync(WsA, provider, clock, refresh: true);
        Assert.Equal(2, provider.Fetches);
    }

    // ---- Keeping storage in step ----

    [Fact]
    public async Task A_known_profile_is_brought_up_to_date_in_place_and_one_no_longer_listed_is_kept_as_removed()
    {
        var provider = new ScriptedProvider(WsA, Listing(Instagram(), Facebook()));
        var clock = new MovableClock();
        var before = Mine(await ReadAsync(WsA, provider, clock), provider);
        var instagramId = before.Single(profile => profile.Platform is PublishingPlatform.Instagram).Id;
        var facebookId = before.Single(profile => profile.Platform is PublishingPlatform.Facebook).Id;

        // Renamed and disconnected at the provider; the Facebook profile is gone from its list.
        provider.Next = Listing(Instagram(name: "Cozy Fall, renamed") with { Status = PublishingProfileStatus.NeedsReconnecting });
        clock.Advance(PublishingPolicy.ProfilesFreshFor);
        var after = Mine(await ReadAsync(WsA, provider, clock), provider);

        var kept = Assert.Single(after);
        Assert.Equal(instagramId, kept.Id);
        Assert.Equal("Cozy Fall, renamed", kept.DisplayName);
        Assert.Equal(PublishingProfileStatus.NeedsReconnecting, kept.Status);
        Assert.Equal(clock.UtcNow, kept.LastSeenAt);

        // Removed, not deleted: a publishing record may still point at it.
        var removed = await InAsync(WsA, db => db.PublishingProfiles.AsNoTracking().SingleAsync(profile => profile.Id == facebookId, Ct));
        Assert.Equal(PublishingProfileStatus.Removed, removed.Status);
        Assert.True(removed.LastSeenAt < clock.UtcNow);

        // Listed again later, it is the same profile, not a second one.
        provider.Next = Listing(Instagram(), Facebook());
        clock.Advance(PublishingPolicy.ProfilesFreshFor);
        var back = Mine(await ReadAsync(WsA, provider, clock), provider);

        Assert.Equal(facebookId, back.Single(profile => profile.Platform is PublishingPlatform.Facebook).Id);
        Assert.Equal(2, await InAsync(WsA, db => db.PublishingProfiles.CountAsync(profile => profile.ProviderKey == provider.Key, Ct)));
    }

    [Fact]
    public async Task A_read_that_was_cut_short_updates_and_adds_but_never_removes()
    {
        var provider = new ScriptedProvider(WsA, Listing(Instagram(), Facebook()));
        var clock = new MovableClock();
        await ReadAsync(WsA, provider, clock);

        // The provider now has more than one read takes; this one reached Instagram and something new, not Facebook.
        provider.Next = Listing(Instagram(name: "Cozy Fall, renamed"), Instagram("ch_EXTERNAL_new", "A new one")) with { Truncated = true };
        clock.Advance(PublishingPolicy.ProfilesFreshFor);
        var after = Mine(await ReadAsync(WsA, provider, clock), provider);

        Assert.Equal(3, after.Count);
        Assert.Contains(after, profile => profile.DisplayName == "Cozy Fall, renamed");
        Assert.Contains(after, profile => profile.DisplayName == "A new one");

        // Not reached is not the same as gone.
        Assert.Equal(PublishingProfileStatus.Ready, after.Single(profile => profile.Platform is PublishingPlatform.Facebook).Status);
    }

    [Fact]
    public async Task What_the_provider_says_is_stored_only_as_far_as_it_can_be_trusted()
    {
        var provider = new ScriptedProvider(
            WsA,
            Listing(
                Instagram("ch_blank", name: "   "),
                Instagram("ch_long", name: new string('n', PublishingPolicy.DisplayNameMaxLength + 50)),
                Instagram("ch_zone") with { TimeZoneId = "Mars/Olympus_Mons" },
                Instagram("ch_nozone") with { TimeZoneId = null },

                // The same profile listed twice is one profile.
                Instagram("ch_twice", name: "Listed twice"),
                Instagram("ch_twice", name: "Listed twice")));

        var mine = Mine(await ReadAsync(WsA, provider, new MovableClock()), provider);

        Assert.Equal(5, mine.Count);

        // No name given: the network is what is known, said plainly rather than invented.
        Assert.Contains(mine, profile => profile.DisplayName == "Instagram");
        Assert.Contains(mine, profile => profile.DisplayName.Length == PublishingPolicy.DisplayNameMaxLength);

        // A zone this server cannot compute with is not kept as though it could.
        Assert.Equal(2, mine.Count(profile => profile.TimeZoneId is null));
        Assert.Single(mine, profile => profile.DisplayName == "Listed twice");
    }

    [Fact]
    public async Task Two_reads_at_once_store_each_profile_once()
    {
        var provider = new ScriptedProvider(WsA, Listing(Instagram(), Facebook()));
        var clock = new MovableClock();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => ReadAsync(WsA, provider, clock), Ct)));

        Assert.Equal(2, await InAsync(WsA, db => db.PublishingProfiles.CountAsync(profile => profile.ProviderKey == provider.Key, Ct)));
        Assert.Equal(2, await InAsync(WsA, db => db.ExternalPublishingProfileReferences.CountAsync(reference => reference.ProviderKey == provider.Key, Ct)));
        Assert.Equal(1, await InAsync(WsA, db => db.PublishingProviderSyncs.CountAsync(sync => sync.ProviderKey == provider.Key, Ct)));

        // Every caller is told about the same two profiles, whichever of them did the writing.
        var ids = Mine(results[0], provider).Select(profile => profile.Id).Order().ToList();
        Assert.Equal(2, ids.Count);
        Assert.All(results, result => Assert.Equal(ids, Mine(result, provider).Select(profile => profile.Id).Order()));
    }

    // ---- When the provider lets the read down ----

    [Fact]
    public async Task A_provider_that_cannot_be_reached_changes_the_status_and_nothing_that_was_stored()
    {
        var provider = new ScriptedProvider(WsA, Listing(Instagram(), Facebook()));
        var clock = new MovableClock();
        var good = await ReadAsync(WsA, provider, clock);

        provider.Next = PublishingProfileFetch.Of(PublishingProfileFetchOutcome.Unavailable);
        clock.Advance(PublishingPolicy.ProfilesFreshFor);
        var down = await ReadAsync(WsA, provider, clock);

        Assert.Equal(PublishingConnectionStatus.Unavailable, down.Connection.Status);
        Assert.Equal(clock.UtcNow, down.Connection.LastCheckedAt);
        Assert.Equal(good.Connection.LastSucceededAt, down.Connection.LastSucceededAt);
        Assert.Equal((int)PublishingPolicy.UnavailableBackoff.TotalSeconds, down.Connection.RetryAfterSeconds);

        // Not "removed": a provider that did not answer has not said anything was removed.
        Assert.Equal(Mine(good, provider), Mine(down, provider));

        // Left alone for the backoff, then asked again without anyone having to say so.
        clock.Advance(PublishingPolicy.UnavailableBackoff - TimeSpan.FromSeconds(1));
        await ReadAsync(WsA, provider, clock);
        Assert.Equal(2, provider.Fetches);

        provider.Next = Listing(Instagram(), Facebook());
        clock.Advance(TimeSpan.FromSeconds(1));
        var recovered = await ReadAsync(WsA, provider, clock);

        Assert.Equal(3, provider.Fetches);
        Assert.Equal(PublishingConnectionStatus.Connected, recovered.Connection.Status);
        Assert.Null(recovered.Connection.RetryAfterSeconds);
    }

    [Fact]
    public async Task A_throttle_is_obeyed_for_as_long_as_the_provider_asked_and_a_refresh_does_not_override_it()
    {
        var provider = new ScriptedProvider(WsA, PublishingProfileFetch.Of(PublishingProfileFetchOutcome.RateLimited, TimeSpan.FromHours(1)));
        var clock = new MovableClock();

        var throttled = await ReadAsync(WsA, provider, clock);

        Assert.Equal(PublishingConnectionStatus.RateLimited, throttled.Connection.Status);
        Assert.Equal(3600, throttled.Connection.RetryAfterSeconds);
        Assert.Null(throttled.Connection.LastSucceededAt);

        clock.Advance(TimeSpan.FromMinutes(59));
        var waiting = await ReadAsync(WsA, provider, clock, refresh: true);
        Assert.Equal(1, provider.Fetches);
        Assert.Equal(60, waiting.Connection.RetryAfterSeconds);

        provider.Next = Listing(Instagram());
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(PublishingConnectionStatus.Connected, (await ReadAsync(WsA, provider, clock)).Connection.Status);
        Assert.Equal(2, provider.Fetches);
    }

    [Theory]
    [InlineData(5, 60)]
    [InlineData(600, 600)]
    [InlineData(60 * 60 * 24 * 3, 60 * 60 * 24)]
    [InlineData(null, 15 * 60)]
    public async Task A_providers_retry_after_is_believed_within_limits(int? providerSeconds, int expectedSeconds)
    {
        var provider = new ScriptedProvider(
            WsA,
            PublishingProfileFetch.Of(
                PublishingProfileFetchOutcome.RateLimited,
                providerSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null));

        var result = await ReadAsync(WsA, provider, new MovableClock());

        Assert.Equal(expectedSeconds, result.Connection.RetryAfterSeconds);
    }

    [Fact]
    public async Task A_refused_credential_is_left_alone_for_a_while_unless_a_refresh_is_asked_for()
    {
        var provider = new ScriptedProvider(WsA, PublishingProfileFetch.Of(PublishingProfileFetchOutcome.CredentialRejected));
        var clock = new MovableClock();

        var refused = await ReadAsync(WsA, provider, clock);
        Assert.Equal(PublishingConnectionStatus.CredentialRejected, refused.Connection.Status);
        Assert.Equal((int)PublishingPolicy.RefusedBackoff.TotalSeconds, refused.Connection.RetryAfterSeconds);

        clock.Advance(TimeSpan.FromMinutes(5));
        await ReadAsync(WsA, provider, clock);
        Assert.Equal(1, provider.Fetches);

        // An operator has replaced the key and somebody asks again: this server's own caution gives way.
        provider.Next = Listing(Instagram());
        var fixedNow = await ReadAsync(WsA, provider, clock, refresh: true);

        Assert.Equal(2, provider.Fetches);
        Assert.Equal(PublishingConnectionStatus.Connected, fixedNow.Connection.Status);
    }

    // ---- Whose provider it is ----

    [Fact]
    public async Task A_workspace_the_credential_does_not_belong_to_is_not_configured_and_the_provider_is_never_asked()
    {
        var provider = new ScriptedProvider(WsA, Listing(Instagram(), Facebook()));
        var clock = new MovableClock();
        await ReadAsync(WsA, provider, clock);

        var other = await ReadAsync(WsB, provider, clock, refresh: true);

        Assert.Equal(1, provider.Fetches);
        Assert.Equal(
            new PublishingConnectionServiceModel(null, PublishingConnectionStatus.NotConfigured, null, null, null),
            other.Connection);

        // None of A's profiles, mappings or sync state is visible from B.
        Assert.Empty(Mine(other, provider));
        Assert.Equal(0, await InAsync(WsB, db => db.PublishingProfiles.CountAsync(profile => profile.ProviderKey == provider.Key, Ct)));
        Assert.Equal(0, await InAsync(WsB, db => db.ExternalPublishingProfileReferences.CountAsync(reference => reference.ProviderKey == provider.Key, Ct)));
        Assert.Equal(0, await InAsync(WsB, db => db.PublishingProviderSyncs.CountAsync(sync => sync.ProviderKey == provider.Key, Ct)));
    }

    [Fact]
    public async Task The_credential_belongs_to_a_workspace_by_id_so_sharing_its_slug_or_having_no_owner_gets_nothing()
    {
        // The key was configured for a workspace that has since gone. Another now answers to the slug that one
        // had — created under the same name — and reads as "workspace-a" with an id of its own. It is not the
        // owner, and what it is called does not make it so.
        var formerOwner = Guid.NewGuid();
        var orphaned = new ScriptedProvider(formerOwner, Listing(Instagram()));
        await using (var scope = fixture.ScopeFor(WsA))
        {
            var reader = scope.ServiceProvider.GetRequiredService<IWorkspaceContext>();
            Assert.Equal("workspace-a", reader.WorkspaceSlug);
            Assert.NotEqual(formerOwner, reader.WorkspaceId);
        }

        var claimed = await ReadAsync(WsA, orphaned, new MovableClock(), refresh: true);

        Assert.Equal(0, orphaned.Fetches);
        Assert.Equal(PublishingConnectionStatus.NotConfigured, claimed.Connection.Status);
        Assert.Null(claimed.Connection.ProviderKey);

        var provider = new ScriptedProvider(WsA, Listing(Instagram()));

        // A provider that names no workspace, or the empty id, is nobody's.
        foreach (var owner in new Guid?[] { null, Guid.Empty })
        {
            var unowned = new ScriptedProvider(owner, Listing(Instagram()));
            var result = await ReadAsync(WsA, unowned, new MovableClock());

            Assert.Equal(0, unowned.Fetches);
            Assert.Equal(PublishingConnectionStatus.NotConfigured, result.Connection.Status);
        }

        // And its real owner is still served.
        Assert.Equal(PublishingConnectionStatus.Connected, (await ReadAsync(WsA, provider, new MovableClock())).Connection.Status);
        Assert.Equal(1, provider.Fetches);
    }

    [Fact]
    public async Task What_the_owning_workspace_stored_is_invisible_to_the_other_whichever_of_them_reads_last()
    {
        // B is the owner this time, and has read: its rows exist before A ever asks.
        var provider = new ScriptedProvider(WsB, Listing(Instagram(), Facebook()));
        var clock = new MovableClock();
        Assert.Equal(2, Mine(await ReadAsync(WsB, provider, clock), provider).Count);
        var syncBefore = await InAsync(WsB, db => db.PublishingProviderSyncs.AsNoTracking().SingleAsync(sync => sync.ProviderKey == provider.Key, Ct));

        clock.Advance(PublishingPolicy.ProfilesFreshFor);
        var fromA = await ReadAsync(WsA, provider, clock, refresh: true);

        Assert.Equal(PublishingConnectionStatus.NotConfigured, fromA.Connection.Status);
        Assert.Empty(Mine(fromA, provider));
        Assert.Equal(1, provider.Fetches);

        // A's read wrote nothing of its own and moved nothing of B's.
        Assert.Equal(0, await InAsync(WsA, db => db.PublishingProviderSyncs.CountAsync(sync => sync.ProviderKey == provider.Key, Ct)));
        var syncAfter = await InAsync(WsB, db => db.PublishingProviderSyncs.AsNoTracking().SingleAsync(sync => sync.ProviderKey == provider.Key, Ct));
        Assert.Equal(syncBefore.LastAttemptAt, syncAfter.LastAttemptAt);
        Assert.Equal(syncBefore.RowVersion, syncAfter.RowVersion);
    }

    [Fact]
    public async Task Profiles_read_before_are_still_listed_once_the_provider_is_no_longer_configured()
    {
        var provider = new ScriptedProvider(WsA, Listing(Instagram()));
        var clock = new MovableClock();
        var before = Mine(await ReadAsync(WsA, provider, clock), provider);

        // The key is taken away: this host now has no provider at all.
        var after = await ReadAsync(WsA, new UnconfiguredPublishingProfileGateway(), clock);

        Assert.Equal(PublishingConnectionStatus.NotConfigured, after.Connection.Status);
        Assert.Null(after.Connection.ProviderKey);
        Assert.Equal(before, Mine(after, provider));
    }

    // ---- What the engine itself refuses ----

    [Fact]
    public async Task The_engine_refuses_a_second_mapping_for_one_provider_profile_and_a_mapping_into_another_workspace()
    {
        var provider = new ScriptedProvider(WsA, Listing(Instagram()));
        var profileId = Mine(await ReadAsync(WsA, provider, new MovableClock()), provider).Single().Id;

        // The same provider profile, mapped again in the same workspace.
        await InAsync(WsA, async db =>
        {
            var second = NewProfile(provider.Key);
            db.AddRange(second, Reference(second.Id, provider.Key, "ch_EXTERNAL_ig"));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));

            return 0;
        });

        // B pointing a mapping at A's profile: the composite key has no such row to point at.
        await InAsync(WsB, async db =>
        {
            db.Add(Reference(profileId, provider.Key, "ch_EXTERNAL_other"));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));

            return 0;
        });

        // The same provider profile in the other workspace is its own row there, not a clash.
        await InAsync(WsB, async db =>
        {
            var theirs = NewProfile(provider.Key);
            db.AddRange(theirs, Reference(theirs.Id, provider.Key, "ch_EXTERNAL_ig"));
            await db.SaveChangesAsync(Ct);

            return 0;
        });
    }

    [Theory]
    [InlineData("blank name")]
    [InlineData("no platform")]
    [InlineData("status past the last")]
    [InlineData("sync that was never an attempt")]
    public async Task The_engine_refuses_rows_whose_state_does_not_hold_together(string fault)
    {
        var key = $"t-{Guid.NewGuid():N}"[..14];
        var profile = NewProfile(key);
        object row = profile;

        switch (fault)
        {
            case "blank name": profile.DisplayName = "  "; break;
            case "no platform": profile.Platform = default; break;
            case "status past the last": profile.Status = (PublishingProfileStatus)5; break;
            default:
                row = new PublishingProviderSync
                {
                    Id = Guid.NewGuid(), ProviderKey = key, LastAttemptAt = SqlServerRecipeFixture.Now,
                    LastOutcome = PublishingConnectionStatus.NotConfigured,
                };
                break;
        }

        await InAsync(WsA, async db =>
        {
            db.Add(row);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));

            return 0;
        });
    }

    // ---- Helpers ----

    private static PublishingProfile NewProfile(string providerKey) => new()
    {
        Id = Guid.NewGuid(), ProviderKey = providerKey, Platform = PublishingPlatform.Instagram, DisplayName = "Cozy Fall",
        Status = PublishingProfileStatus.Ready, FirstSeenAt = SqlServerRecipeFixture.Now, LastSeenAt = SqlServerRecipeFixture.Now,
    };

    private static ExternalPublishingProfileReference Reference(Guid profileId, string providerKey, string externalId) => new()
    {
        Id = Guid.NewGuid(), PublishingProfileId = profileId, ProviderKey = providerKey, ExternalId = externalId, ExternalAccountId = "org_EXTERNAL_1",
    };

    /// <summary>The profiles this test's own provider put there: the fixture is shared.</summary>
    private static List<PublishingProfileServiceModel> Mine(PublishingProfilesServiceModel result, ScriptedProvider provider) =>
        [.. result.Profiles.Where(profile => profile.ProviderKey == provider.Key)];

    /// <summary>One read through Business, DataLayer and Repository, in one workspace's resolved scope.</summary>
    private async Task<PublishingProfilesServiceModel> ReadAsync(
        Guid workspaceId, IPublishingProfileGateway gateway, MovableClock clock, bool refresh = false)
    {
        await using var scope = fixture.ScopeFor(workspaceId);

        return await ReadInAsync(scope, gateway, clock, refresh);
    }

    private static async Task<PublishingProfilesServiceModel> ReadInAsync(
        AsyncServiceScope scope, IPublishingProfileGateway gateway, MovableClock clock, bool refresh)
    {
        var db = SqlServerRecipeFixture.Db(scope);

        var business = new PublishingProfileBusiness(
            new PublishingProfileDataLayer(
                new PublishingProfileRepository(db), gateway, db, NullLogger<PublishingProfileDataLayer>.Instance),
            Zones,
            scope.ServiceProvider.GetRequiredService<IWorkspaceContext>(),
            clock);

        return await business.GetAsync(refresh, Ct);
    }

    private static readonly ITimeZoneConverter Zones =
        new ServiceCollection().AddApplicationTime().BuildServiceProvider().GetRequiredService<ITimeZoneConverter>();

    private async Task<T> InAsync<T>(Guid workspaceId, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = fixture.ScopeFor(workspaceId);

        return await work(SqlServerRecipeFixture.Db(scope));
    }

    private sealed class MovableClock : IClock
    {
        private long _ticks = SqlServerRecipeFixture.Now.UtcTicks;

        public DateTimeOffset UtcNow => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>A provider that answers what the test says, bound to the workspace the test says, under a key of its own.</summary>
    private sealed class ScriptedProvider(Guid? workspaceId, PublishingProfileFetch next) : IPublishingProfileGateway
    {
        private int _fetches;

        public string Key { get; } = $"t-{Guid.NewGuid():N}"[..14];

        public PublishingProfileFetch Next { get; set; } = next;

        public int Fetches => _fetches;

        public PublishingProviderDescriptor Describe() => new(true, Key, workspaceId);

        public Task<PublishingProfileFetch> FetchAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _fetches);

            return Task.FromResult(Next);
        }
    }
}
