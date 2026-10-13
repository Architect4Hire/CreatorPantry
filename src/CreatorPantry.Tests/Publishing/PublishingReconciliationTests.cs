using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Publishing.Business;
using CreatorPantry.Domain.Modules.Publishing.Data;
using CreatorPantry.Domain.Modules.Publishing.Data.Entities;
using CreatorPantry.Domain.Modules.Publishing.Facade;
using CreatorPantry.Domain.Modules.Publishing.Gateways;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CreatorPantry.Tests.Publishing;

/// <summary>
/// 13.10c against a real SQL Server: what the worker may claim, and what it is allowed to conclude from what
/// the provider says — and from what it does not say.
/// </summary>
/// <remarks>
/// <para>
/// The two rules these tests exist to hold. **Nothing is concluded from a listing that did not happen**, or
/// that was cut short: a record is only ever settled on evidence. And **a record is never resubmitted on the
/// strength of an absence** — an unacknowledged post becomes failed, which a creator may confirm again, rather
/// than being sent a second time at the risk of their audience seeing it twice.
/// </para>
/// <para>
/// The fixture is shared, so each test seeds its own records and every assertion is scoped to them.
/// </para>
/// </remarks>
public sealed class PublishingReconciliationTests(SqlServerRecipeFixture fixture) : IClassFixture<SqlServerRecipeFixture>
{
    private const string Zone = "America/Chicago";
    private static readonly Guid WsA = SqlServerRecipeFixture.WorkspaceA;
    private static readonly Guid WsB = SqlServerRecipeFixture.WorkspaceB;
    private static readonly DateTimeOffset Now = SqlServerRecipeFixture.Now;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- What may be claimed ----

    [Fact]
    public async Task Only_records_with_something_outstanding_are_claimed()
    {
        var profile = await SeedProfileAsync(WsA);
        var claimable = new Dictionary<PublishingRecordStatus, Guid>();
        var terminal = new Dictionary<PublishingRecordStatus, Guid>();

        foreach (var status in new[]
                 {
                     PublishingRecordStatus.Pending,
                     PublishingRecordStatus.Submitting,
                     PublishingRecordStatus.Scheduled,
                     PublishingRecordStatus.Cancelling,
                 })
        {
            claimable[status] = await SeedRecordAsync(WsA, profile, status, availableAt: Now);
        }

        foreach (var status in new[]
                 {
                     PublishingRecordStatus.Published,
                     PublishingRecordStatus.Cancelled,
                     PublishingRecordStatus.Failed,
                     PublishingRecordStatus.NeedsAttention,
                 })
        {
            terminal[status] = await SeedRecordAsync(WsA, profile, status, availableAt: Now);
        }

        var claimed = await ClaimAsync(Now);
        var ids = claimed.Select(claim => claim.RecordId).ToHashSet();

        Assert.All(claimable.Values, id => Assert.Contains(id, ids));

        // Terminal records, and ones waiting for a person, are never picked up again — which is what makes
        // "needs attention" a resting place rather than a loop.
        Assert.All(terminal.Values, id => Assert.DoesNotContain(id, ids));
    }

    [Fact]
    public async Task A_record_that_is_not_due_yet_is_left_alone()
    {
        var profile = await SeedProfileAsync(WsA);
        var later = await SeedRecordAsync(
            WsA, profile, PublishingRecordStatus.Pending, availableAt: Now.AddMinutes(30));

        var claimed = await ClaimAsync(Now);

        Assert.DoesNotContain(later, claimed.Select(claim => claim.RecordId));
    }

    [Fact]
    public async Task A_claimed_record_is_not_claimed_again_until_its_lease_lapses()
    {
        var profile = await SeedProfileAsync(WsA);
        var recordId = await SeedRecordAsync(WsA, profile, PublishingRecordStatus.Scheduled, availableAt: Now);

        Assert.Contains(recordId, (await ClaimAsync(Now)).Select(claim => claim.RecordId));

        // The guard is the claim: a second worker in the same moment gets nothing.
        Assert.DoesNotContain(recordId, (await ClaimAsync(Now)).Select(claim => claim.RecordId));

        // And a worker that died holding it does not strand it for ever.
        var afterLease = Now + PublishingLedgerPolicy.ReconcileLeaseDuration + TimeSpan.FromSeconds(1);
        Assert.Contains(recordId, (await ClaimAsync(afterLease)).Select(claim => claim.RecordId));
    }

    [Fact]
    public async Task Maintenance_releases_a_lapsed_claim_without_touching_its_state_or_its_budget()
    {
        var profile = await SeedProfileAsync(WsA);
        var recordId = await SeedRecordAsync(WsA, profile, PublishingRecordStatus.Submitting, availableAt: Now);

        await ClaimAsync(Now);

        var before = await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(record => record.Id == recordId, Ct));

        await using var scope = fixture.ScopeFor(WsA);
        var released = await new PublishingReconciliationClaimRepository(SqlServerRecipeFixture.Db(scope))
            .ReleaseLapsedAsync(Now + PublishingLedgerPolicy.ReconcileLeaseDuration + TimeSpan.FromSeconds(1), Ct);

        Assert.True(released >= 1);

        var after = await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(record => record.Id == recordId, Ct));

        // The lease goes and nothing else: no status moves and no attempt is spent, because nobody knows how
        // far the dead worker got. The record's own state still says what is in doubt.
        Assert.Null(after.LeasedBy);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.SubmitAttempts, after.SubmitAttempts);
        Assert.Equal(before.UncertainOperation, after.UncertainOperation);
    }

    [Fact]
    public async Task A_claim_carries_identifiers_and_queue_state_and_nothing_a_creator_wrote()
    {
        var profile = await SeedProfileAsync(WsA);
        var recordId = await SeedRecordAsync(
            WsA, profile, PublishingRecordStatus.Pending, availableAt: Now, text: "SECRET_CREATOR_WORDS");

        var claim = Assert.Single(
            (await ClaimAsync(Now)).Where(candidate => candidate.RecordId == recordId));

        // The condition the IgnoreQueryFilters carve-out carries: a worker that has not resolved a workspace
        // yet never sees anything from inside it.
        Assert.Equal(WsA, claim.WorkspaceId);
        Assert.Equal(profile.Id, claim.PublishingProfileId);
        Assert.DoesNotContain(
            "SECRET_CREATOR_WORDS",
            System.Text.Json.JsonSerializer.Serialize(claim),
            StringComparison.Ordinal);
    }

    // ---- An unacknowledged submit ----

    [Fact]
    public async Task A_post_the_provider_turns_out_to_have_is_recorded_with_its_id()
    {
        var profile = await SeedProfileAsync(WsA);
        var recordId = await SeedRecordAsync(
            WsA,
            profile,
            PublishingRecordStatus.Submitting,
            availableAt: Now,
            uncertain: PublishingOperation.Submit,
            uncertainSince: Now.AddMinutes(-30));

        var provider = Listing(Post("update_EXTERNAL_R1", PublishingPostState.Scheduled));

        var summary = await ReconcileAsync(WsA, provider, profile.Id, [recordId]);

        Assert.Equal(1, summary.StillWaiting);

        var record = await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(item => item.Id == recordId, Ct));

        // The post exists after all: the record stops being uncertain and starts being a scheduled post.
        Assert.Equal(PublishingRecordStatus.Scheduled, record.Status);
        Assert.Null(record.UncertainOperation);

        // And the id this server never heard is now where provider ids live.
        var reference = await InAsync(WsA, db => db.ExternalPublicationReferences.AsNoTracking()
            .SingleAsync(item => item.PublishingRecordId == recordId, Ct));
        Assert.Equal("update_EXTERNAL_R1", reference.ExternalUpdateId);
    }

    [Fact]
    public async Task A_post_that_turns_out_to_have_gone_out_is_published()
    {
        var profile = await SeedProfileAsync(WsA);
        var recordId = await SeedRecordAsync(
            WsA,
            profile,
            PublishingRecordStatus.Submitting,
            availableAt: Now,
            uncertain: PublishingOperation.Submit,
            uncertainSince: Now.AddMinutes(-30));

        var summary = await ReconcileAsync(
            WsA, Listing(Post("update_EXTERNAL_R2", PublishingPostState.Sent)), profile.Id, [recordId]);

        Assert.Equal(1, summary.Settled);

        var record = await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(item => item.Id == recordId, Ct));
        Assert.Equal(PublishingRecordStatus.Published, record.Status);
        Assert.NotNull(record.CompletedAt);
        Assert.Null(record.UncertainOperation);
    }

    [Fact]
    public async Task A_post_the_provider_never_acknowledged_fails_and_is_never_sent_again()
    {
        var profile = await SeedProfileAsync(WsA);
        var recordId = await SeedRecordAsync(
            WsA,
            profile,
            PublishingRecordStatus.Submitting,
            availableAt: Now,
            uncertain: PublishingOperation.Submit,
            uncertainSince: Now.AddMinutes(-30));

        // A listing read to its end, taken well after the attempt, with no matching post in it.
        var provider = Listing();

        var summary = await ReconcileAsync(WsA, provider, profile.Id, [recordId]);

        Assert.Equal(1, summary.Settled);

        var record = await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(item => item.Id == recordId, Ct));

        Assert.Equal(PublishingRecordStatus.Failed, record.Status);
        Assert.Equal("provider_never_acknowledged", record.FailureCategory);

        // The decision this whole phase turns on: no second post, ever, on the strength of an absence.
        Assert.Empty(provider.Sent);
    }

    [Fact]
    public async Task An_absence_is_not_believed_until_the_provider_has_had_time_to_admit_the_post()
    {
        var profile = await SeedProfileAsync(WsA);
        var recordId = await SeedRecordAsync(
            WsA,
            profile,
            PublishingRecordStatus.Submitting,
            availableAt: Now,
            uncertain: PublishingOperation.Submit,

            // The call was a moment ago: a listing may simply not show it yet.
            uncertainSince: Now.AddSeconds(-5));

        var summary = await ReconcileAsync(WsA, Listing(), profile.Id, [recordId]);

        Assert.Equal(1, summary.StillWaiting);

        var record = await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(item => item.Id == recordId, Ct));
        Assert.Equal(PublishingRecordStatus.Submitting, record.Status);
        Assert.Equal(PublishingOperation.Submit, record.UncertainOperation);
    }

    [Fact]
    public async Task An_absence_from_a_listing_that_was_cut_short_proves_nothing()
    {
        var profile = await SeedProfileAsync(WsA);
        var recordId = await SeedRecordAsync(
            WsA,
            profile,
            PublishingRecordStatus.Submitting,
            availableAt: Now,
            uncertain: PublishingOperation.Submit,
            uncertainSince: Now.AddMinutes(-30));

        // Posts, but not ours, and more pages than the listing read: the match may be past the cap.
        var provider = new ScriptedProvider(WsA)
        {
            NextListing = new PublishingPostLookupResult(
                PublishingPostLookupOutcome.Listed,
                [Post("update_EXTERNAL_SOMEONE_ELSE", PublishingPostState.Scheduled, text: "Another post entirely")],
                Complete: false),
        };

        var summary = await ReconcileAsync(WsA, provider, profile.Id, [recordId]);

        Assert.Equal(1, summary.StillWaiting);
        Assert.Equal(
            PublishingRecordStatus.Submitting,
            (await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
                .SingleAsync(item => item.Id == recordId, Ct))).Status);
    }

    [Theory]
    [InlineData(PublishingPostLookupOutcome.RateLimited)]
    [InlineData(PublishingPostLookupOutcome.Unavailable)]
    public async Task A_listing_that_did_not_happen_settles_nothing(PublishingPostLookupOutcome outcome)
    {
        var profile = await SeedProfileAsync(WsA);
        var recordId = await SeedRecordAsync(
            WsA,
            profile,
            PublishingRecordStatus.Submitting,
            availableAt: Now,
            uncertain: PublishingOperation.Submit,
            uncertainSince: Now.AddMinutes(-30));

        var provider = new ScriptedProvider(WsA)
        {
            NextListing = PublishingPostLookupResult.Of(outcome, TimeSpan.FromMinutes(3)),
        };

        var summary = await ReconcileAsync(WsA, provider, profile.Id, [recordId]);

        Assert.Equal(1, summary.Unavailable);

        var record = await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(item => item.Id == recordId, Ct));

        Assert.Equal(PublishingRecordStatus.Submitting, record.Status);
        Assert.Equal(PublishingOperation.Submit, record.UncertainOperation);

        // Backed off and handed back, not concluded about.
        Assert.True(record.AvailableAt > Now);
        Assert.Null(record.LeasedBy);
    }

    // ---- A post that was accepted ----

    [Theory]
    [InlineData(PublishingPostState.Sent, PublishingRecordStatus.Published)]
    [InlineData(PublishingPostState.Failed, PublishingRecordStatus.Failed)]
    public async Task A_scheduled_record_takes_the_state_its_post_reports(
        PublishingPostState postState, PublishingRecordStatus expected)
    {
        var profile = await SeedProfileAsync(WsA);

        // One id per case: the mapping is unique per workspace and provider, and this fixture is shared.
        var externalId = $"update_EXTERNAL_R3_{postState}";
        var recordId = await SeedRecordAsync(
            WsA, profile, PublishingRecordStatus.Scheduled, availableAt: Now, externalUpdateId: externalId);

        var summary = await ReconcileAsync(
            WsA, Listing(Post(externalId, postState)), profile.Id, [recordId]);

        Assert.Equal(1, summary.Settled);
        Assert.Equal(
            expected,
            (await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
                .SingleAsync(item => item.Id == recordId, Ct))).Status);
    }

    [Fact]
    public async Task A_scheduled_post_the_provider_no_longer_has_is_cancelled_once_its_time_has_passed()
    {
        var profile = await SeedProfileAsync(WsA);
        var recordId = await SeedRecordAsync(
            WsA,
            profile,
            PublishingRecordStatus.Scheduled,
            availableAt: Now,
            externalUpdateId: "update_EXTERNAL_R4",

            // Due well before now, so the grace after its due time has run out: only then does an absence
            // from a complete listing mean the post is gone.
            scheduledAt: Now.AddHours(-1));

        var summary = await ReconcileAsync(WsA, Listing(), profile.Id, [recordId]);

        Assert.Equal(1, summary.Settled);

        var record = await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(item => item.Id == recordId, Ct));

        // Somebody removed it in the provider's own interface. It will not go out, which is what cancelled
        // means, and the category says it was not cancelled from here.
        Assert.Equal(PublishingRecordStatus.Cancelled, record.Status);
        Assert.Equal("provider_post_absent", record.FailureCategory);
    }

    [Fact]
    public async Task A_post_still_waiting_to_go_out_is_polled_again_later()
    {
        var profile = await SeedProfileAsync(WsA);
        var recordId = await SeedRecordAsync(
            WsA, profile, PublishingRecordStatus.Scheduled, availableAt: Now, externalUpdateId: "update_EXTERNAL_R5");

        var summary = await ReconcileAsync(
            WsA, Listing(Post("update_EXTERNAL_R5", PublishingPostState.Scheduled)), profile.Id, [recordId]);

        Assert.Equal(1, summary.StillWaiting);

        var record = await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(item => item.Id == recordId, Ct));
        Assert.Equal(PublishingRecordStatus.Scheduled, record.Status);
        Assert.True(record.AvailableAt > Now);
        Assert.Null(record.LeasedBy);
    }

    [Fact]
    public async Task A_post_missing_from_a_listing_at_the_moment_it_is_due_is_not_declared_gone()
    {
        var profile = await SeedProfileAsync(WsA);

        // Due right now, and the provider's listing does not show it — which is exactly what a provider
        // moving a post through a state of its own at send time would look like.
        var recordId = await SeedRecordAsync(
            WsA,
            profile,
            PublishingRecordStatus.Scheduled,
            availableAt: Now,
            externalUpdateId: "update_EXTERNAL_DUE",
            scheduledAt: Now);

        var summary = await ReconcileAsync(WsA, Listing(), profile.Id, [recordId]);

        // The grace runs from the due time, not from when the record was made: a confirmation is always older
        // than the post's own moment, so measuring from it would give a scheduled post no grace at all.
        Assert.Equal(1, summary.StillWaiting);

        var record = await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(item => item.Id == recordId, Ct));
        Assert.Equal(PublishingRecordStatus.Scheduled, record.Status);
        Assert.Null(record.CompletedAt);
    }

    [Fact]
    public async Task The_window_only_runs_out_on_a_listing_that_was_read_to_its_end()
    {
        var profile = await SeedProfileAsync(WsA);
        var recordId = await SeedRecordAsync(
            WsA,
            profile,
            PublishingRecordStatus.Submitting,
            availableAt: Now,
            uncertain: PublishingOperation.Submit,
            uncertainSince: Now.AddMinutes(-30),
            scheduledAt: Now - PublishingLedgerPolicy.ReconcileWindow - TimeSpan.FromDays(1));

        // Long past the window, but on listings nobody could finish reading. A run of truncated listings is
        // not the provider failing to account for the post.
        var provider = new ScriptedProvider(WsA)
        {
            NextListing = new PublishingPostLookupResult(PublishingPostLookupOutcome.Listed, [], Complete: false),
        };

        var summary = await ReconcileAsync(WsA, provider, profile.Id, [recordId]);

        Assert.Equal(1, summary.StillWaiting);
        Assert.Equal(
            PublishingRecordStatus.Submitting,
            (await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
                .SingleAsync(item => item.Id == recordId, Ct))).Status);
    }

    [Fact]
    public async Task A_record_of_another_profile_is_never_settled_from_this_channels_listing()
    {
        var mine = await SeedProfileAsync(WsA);
        var other = await SeedProfileAsync(WsA);

        var strayId = await SeedRecordAsync(
            WsA, other, PublishingRecordStatus.Scheduled, availableAt: Now, externalUpdateId: "update_EXTERNAL_STRAY",
            scheduledAt: Now.AddHours(-1));

        // One listing is read for one channel. A record of another profile in the batch must be left alone,
        // because an absence from the wrong channel's listing is not an absence at all.
        var summary = await ReconcileAsync(WsA, Listing(), mine.Id, [strayId]);

        Assert.Equal(1, summary.Skipped);

        var record = await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(item => item.Id == strayId, Ct));
        Assert.Equal(PublishingRecordStatus.Scheduled, record.Status);

        // And it gives up its claim, so it is not re-taken on every pass.
        Assert.Null(record.LeasedBy);
        Assert.True(record.AvailableAt > Now);
    }

    [Fact]
    public async Task A_record_this_pass_may_not_act_on_gives_up_its_claim_and_waits()
    {
        var profile = await SeedProfileAsync(WsB);
        var recordId = await SeedRecordAsync(
            WsB, profile, PublishingRecordStatus.Pending, availableAt: Now);

        // The credential belongs to workspace A, so none of B's records may be acted on here.
        var provider = new ScriptedProvider(WsA);
        var summary = await ReconcileAsync(WsB, provider, profile.Id, [recordId]);

        Assert.Equal(1, summary.Skipped);
        Assert.Empty(provider.Listings);

        var record = await InAsync(WsB, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(item => item.Id == recordId, Ct));

        // Untouched in state, but not left at the front of the queue: a record re-claimed every lease cycle
        // spins, and fills the batch the credential's own workspace is waiting for.
        Assert.Equal(PublishingRecordStatus.Pending, record.Status);
        Assert.Null(record.LeasedBy);
        Assert.True(record.AvailableAt > Now);
    }

    // ---- A cancellation nobody confirmed ----

    [Fact]
    public async Task A_cancellation_whose_post_is_gone_is_settled_as_cancelled()
    {
        var profile = await SeedProfileAsync(WsA);
        var recordId = await SeedRecordAsync(
            WsA,
            profile,
            PublishingRecordStatus.Cancelling,
            availableAt: Now,
            uncertain: PublishingOperation.Cancel,
            uncertainSince: Now.AddMinutes(-30),
            externalUpdateId: "update_EXTERNAL_R6");

        var summary = await ReconcileAsync(WsA, Listing(), profile.Id, [recordId]);

        Assert.Equal(1, summary.Settled);

        var record = await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(item => item.Id == recordId, Ct));
        Assert.Equal(PublishingRecordStatus.Cancelled, record.Status);
        Assert.Null(record.UncertainOperation);
    }

    [Fact]
    public async Task A_cancellation_whose_post_is_still_there_asks_the_provider_again()
    {
        var profile = await SeedProfileAsync(WsA);
        var recordId = await SeedRecordAsync(
            WsA,
            profile,
            PublishingRecordStatus.Cancelling,
            availableAt: Now,
            uncertain: PublishingOperation.Cancel,
            uncertainSince: Now.AddMinutes(-30),
            externalUpdateId: "update_EXTERNAL_R7");

        var provider = Listing(Post("update_EXTERNAL_R7", PublishingPostState.Scheduled));
        provider.NextCancellation = new PublishingCancellationResult(PublishingCancellationOutcome.Cancelled);

        var summary = await ReconcileAsync(WsA, provider, profile.Id, [recordId]);

        Assert.Equal(1, summary.Settled);

        // Safe to repeat, so it is repeated: this is the asymmetry with a submit, which is never retried blind.
        Assert.Equal("update_EXTERNAL_R7", Assert.Single(provider.Removals).ExternalUpdateId);
        Assert.Equal(
            PublishingRecordStatus.Cancelled,
            (await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
                .SingleAsync(item => item.Id == recordId, Ct))).Status);
    }

    [Fact]
    public async Task A_post_that_went_out_before_the_cancellation_landed_says_so()
    {
        var profile = await SeedProfileAsync(WsA);
        var recordId = await SeedRecordAsync(
            WsA,
            profile,
            PublishingRecordStatus.Cancelling,
            availableAt: Now,
            uncertain: PublishingOperation.Cancel,
            uncertainSince: Now.AddMinutes(-30),
            externalUpdateId: "update_EXTERNAL_R8");

        var summary = await ReconcileAsync(
            WsA, Listing(Post("update_EXTERNAL_R8", PublishingPostState.Sent)), profile.Id, [recordId]);

        Assert.Equal(1, summary.Settled);

        var record = await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(item => item.Id == recordId, Ct));

        // Not cancelled: it went out. The category is what stops the board claiming otherwise.
        Assert.Equal(PublishingRecordStatus.Published, record.Status);
        Assert.Equal("cancellation_too_late", record.FailureCategory);
    }

    // ---- Work that is simply due ----

    [Fact]
    public async Task A_record_left_pending_by_a_throttle_is_submitted_and_its_claim_released()
    {
        var profile = await SeedProfileAsync(WsA);
        var recordId = await SeedRecordAsync(WsA, profile, PublishingRecordStatus.Pending, availableAt: Now);

        var provider = Listing();
        provider.Next = new PublishingSubmissionResult(
            PublishingSubmissionOutcome.Scheduled, "update_EXTERNAL_R9", ProviderStatusCode: 200);

        var summary = await ReconcileAsync(WsA, provider, profile.Id, [recordId]);

        Assert.Equal(1, summary.Submitted);

        // No listing was needed to know a pending record needs sending.
        Assert.Empty(provider.Listings);
        Assert.Single(provider.Sent);

        var record = await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(item => item.Id == recordId, Ct));
        Assert.Equal(PublishingRecordStatus.Scheduled, record.Status);
        Assert.Null(record.LeasedBy);
    }

    [Fact]
    public async Task A_profile_the_provider_will_no_longer_post_to_stops_the_submit()
    {
        var profile = await SeedProfileAsync(WsA, PublishingProfileStatus.NeedsReconnecting);
        var recordId = await SeedRecordAsync(WsA, profile, PublishingRecordStatus.Pending, availableAt: Now);

        var provider = Listing();
        var summary = await ReconcileAsync(WsA, provider, profile.Id, [recordId]);

        Assert.Equal(1, summary.NeedsAttention);
        Assert.Empty(provider.Sent);

        var record = await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(item => item.Id == recordId, Ct));
        Assert.Equal(PublishingRecordStatus.NeedsAttention, record.Status);
        Assert.Equal("provider_profile_not_ready", record.FailureCategory);
    }

    // ---- Bounds ----

    [Fact]
    public async Task A_record_nobody_has_settled_within_the_window_becomes_a_persons_problem()
    {
        var profile = await SeedProfileAsync(WsA);
        var recordId = await SeedRecordAsync(
            WsA,
            profile,
            PublishingRecordStatus.Submitting,
            availableAt: Now,
            uncertain: PublishingOperation.Submit,
            uncertainSince: Now.AddMinutes(-30),

            // Scheduled long enough ago that the window has run out.
            scheduledAt: Now - PublishingLedgerPolicy.ReconcileWindow - TimeSpan.FromDays(1));

        var summary = await ReconcileAsync(WsA, Listing(), profile.Id, [recordId]);

        Assert.Equal(1, summary.NeedsAttention);

        var record = await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(item => item.Id == recordId, Ct));

        Assert.Equal(PublishingRecordStatus.NeedsAttention, record.Status);
        Assert.Equal("provider_outcome_unresolved", record.FailureCategory);

        // What a person has to look at is exactly what is still unknown, so it is kept.
        Assert.Equal(PublishingOperation.Submit, record.UncertainOperation);
    }

    [Fact]
    public async Task One_listing_settles_every_record_waiting_on_that_channel()
    {
        var profile = await SeedProfileAsync(WsA);
        var ids = new List<Guid>();

        for (var index = 0; index < 3; index++)
        {
            ids.Add(await SeedRecordAsync(
                WsA, profile, PublishingRecordStatus.Scheduled, availableAt: Now,
                externalUpdateId: $"update_EXTERNAL_BATCH_{index}"));
        }

        var provider = Listing(
            Post("update_EXTERNAL_BATCH_0", PublishingPostState.Sent),
            Post("update_EXTERNAL_BATCH_1", PublishingPostState.Sent),
            Post("update_EXTERNAL_BATCH_2", PublishingPostState.Failed));

        var summary = await ReconcileAsync(WsA, provider, profile.Id, ids);

        // The whole point of grouping by profile: three answers for one call against a small quota.
        Assert.Single(provider.Listings);
        Assert.Equal(3, summary.Settled);
    }

    // ---- Two workspaces ----

    [Fact]
    public async Task A_record_from_a_workspace_the_credential_does_not_belong_to_is_left_untouched()
    {
        var profile = await SeedProfileAsync(WsB);
        var recordId = await SeedRecordAsync(
            WsB,
            profile,
            PublishingRecordStatus.Submitting,
            availableAt: Now,
            uncertain: PublishingOperation.Submit,
            uncertainSince: Now.AddMinutes(-30));

        // The provider is configured, but for workspace A.
        var provider = new ScriptedProvider(WsA);

        var summary = await ReconcileAsync(WsB, provider, profile.Id, [recordId]);

        Assert.Equal(1, summary.Skipped);
        Assert.Empty(provider.Listings);
        Assert.Empty(provider.Sent);

        var record = await InAsync(WsB, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(item => item.Id == recordId, Ct));
        Assert.Equal(PublishingRecordStatus.Submitting, record.Status);
    }

    [Fact]
    public async Task Another_workspaces_record_is_not_reconciled_even_when_its_id_is_handed_over()
    {
        var theirProfile = await SeedProfileAsync(WsB);
        var theirRecord = await SeedRecordAsync(
            WsB, theirProfile, PublishingRecordStatus.Scheduled, availableAt: Now,
            externalUpdateId: "update_EXTERNAL_THEIRS");

        // Workspace A's own provider, asked about a record of B's: the read that matters runs under A's query
        // filter, so there is nothing to find. Finding a row in a claim was never authorization.
        var provider = Listing(Post("update_EXTERNAL_THEIRS", PublishingPostState.Sent));

        var summary = await ReconcileAsync(WsA, provider, theirProfile.Id, [theirRecord]);

        Assert.Equal(1, summary.Skipped);
        Assert.Empty(provider.Listings);

        Assert.Equal(
            PublishingRecordStatus.Scheduled,
            (await InAsync(WsB, db => db.PublishingRecords.AsNoTracking()
                .SingleAsync(item => item.Id == theirRecord, Ct))).Status);
    }

    // ---- Helpers ----

    private static ProviderPost Post(
        string externalId,
        PublishingPostState state,
        string text = "Soda bread, out of the oven at last.",
        DateTimeOffset? dueAt = null) =>
        new(externalId, text, dueAt ?? Now.AddDays(3), state);

    private static ScriptedProvider Listing(params ProviderPost[] posts) =>
        new(WsA)
        {
            NextListing = new PublishingPostLookupResult(
                PublishingPostLookupOutcome.Listed, posts, Complete: true),
        };

    private async Task<IReadOnlyList<PublishingReconciliationClaim>> ClaimAsync(DateTimeOffset now)
    {
        await using var scope = fixture.ScopeFor(WsA);

        return await new PublishingReconciliationClaimRepository(SqlServerRecipeFixture.Db(scope))
            .ClaimDueAsync(Guid.NewGuid(), now, Ct);
    }

    private async Task<PublishingReconcileSummary> ReconcileAsync(
        Guid workspaceId, ScriptedProvider provider, Guid profileId, IReadOnlyList<Guid> recordIds)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        var db = SqlServerRecipeFixture.Db(scope);

        var dataLayer = new PublishingRecordDataLayer(
            new PublishingRecordRepository(db),
            provider,
            scope.ServiceProvider.GetRequiredService<IAuditWriter>(),
            db,
            NullLogger<PublishingRecordDataLayer>.Instance);

        var zones = new ServiceCollection().AddApplicationTime().BuildServiceProvider()
            .GetRequiredService<ITimeZoneConverter>();

        var records = new PublishingRecordBusiness(
            dataLayer,
            scope.ServiceProvider.GetRequiredService<IContentCardLookupFacade>(),
            new NoMedia(),
            new ZonedBrand(),
            zones,
            scope.ServiceProvider.GetRequiredService<IWorkspaceContext>(),
            new FixedClock());

        return await new PublishingReconciliationBusiness(
                dataLayer,
                records,
                scope.ServiceProvider.GetRequiredService<IWorkspaceContext>(),
                new FixedClock())
            .ReconcileProfileAsync(profileId, recordIds, Ct);
    }

    private async Task<SeededProfile> SeedProfileAsync(
        Guid workspaceId, PublishingProfileStatus status = PublishingProfileStatus.Ready)
    {
        var profile = new PublishingProfile
        {
            Id = Guid.NewGuid(),
            ProviderKey = PublishingPolicy.BufferProviderKey,
            Platform = PublishingPlatform.Instagram,
            DisplayName = "The test kitchen",
            TimeZoneId = Zone,
            Status = status,
            FirstSeenAt = Now,
            LastSeenAt = Now,
        };

        var externalId = $"ch_EXTERNAL_{Guid.NewGuid():N}";

        await InAsync(workspaceId, async db =>
        {
            db.PublishingProfiles.Add(profile);
            db.ExternalPublishingProfileReferences.Add(new ExternalPublishingProfileReference
            {
                Id = Guid.NewGuid(),
                PublishingProfileId = profile.Id,
                ProviderKey = PublishingPolicy.BufferProviderKey,
                ExternalId = externalId,
                ExternalAccountId = "org_EXTERNAL_1",
            });
            await db.SaveChangesAsync(Ct);
        });

        return new SeededProfile(profile.Id, externalId);
    }

    private sealed record SeededProfile(Guid Id, string ExternalId);

    /// <summary>A record already in the state a test is about, written directly: no route reaches some of them.</summary>
    private async Task<Guid> SeedRecordAsync(
        Guid workspaceId,
        SeededProfile profile,
        PublishingRecordStatus status,
        DateTimeOffset availableAt,
        PublishingOperation? uncertain = null,
        DateTimeOffset? uncertainSince = null,
        string? externalUpdateId = null,
        string text = "Soda bread, out of the oven at last.",
        DateTimeOffset? scheduledAt = null)
    {
        var record = new PublishingRecord
        {
            Id = Guid.NewGuid(),
            PublishingProfileId = profile.Id,
            Platform = PublishingPlatform.Instagram,
            ContentText = text,
            ScheduledAt = scheduledAt ?? Now.AddDays(3),
            ScheduleTimeZoneId = Zone,
            ScheduledLocal = (scheduledAt ?? Now.AddDays(3)).UtcDateTime.AddHours(-5),
            Status = status,
            StatusChangedAt = Now,
            AvailableAt = availableAt,
            UncertainOperation = uncertain,
            UncertainSince = uncertainSince,
            ConfirmedByMembershipId = SqlServerRecipeFixture.AuthorOne,
            CreatedAt = Now.AddHours(-1),
            SubmitAttempts = uncertain is PublishingOperation.Submit ? 1 : 0,
            CancelAttempts = uncertain is PublishingOperation.Cancel ? 1 : 0,
            FailureCategory = status is PublishingRecordStatus.NeedsAttention ? "provider_timeout" : null,
            IdempotencyKey = $"seeded-{Guid.NewGuid():N}",
        };

        await InAsync(workspaceId, async db =>
        {
            db.PublishingRecords.Add(record);
            await db.SaveChangesAsync(Ct);

            if (externalUpdateId is not null)
            {
                db.ExternalPublicationReferences.Add(new ExternalPublicationReference
                {
                    Id = Guid.NewGuid(),
                    PublishingRecordId = record.Id,
                    ProviderKey = PublishingPolicy.BufferProviderKey,
                    ExternalUpdateId = externalUpdateId,
                    FirstSeenAt = Now,
                });
                await db.SaveChangesAsync(Ct);
            }
        });

        return record.Id;
    }

    private async Task InAsync(Guid workspaceId, Func<CreatorPantryDbContext, Task> work)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        await work(SqlServerRecipeFixture.Db(scope));
    }

    private async Task<T> InAsync<T>(Guid workspaceId, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = fixture.ScopeFor(workspaceId);

        return await work(SqlServerRecipeFixture.Db(scope));
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    /// <summary>The provider, as whatever the test says it holds and answers.</summary>
    private sealed class ScriptedProvider(Guid? workspaceId) : IPublishingPostGateway
    {
        public PublishingSubmissionResult Next { get; set; } =
            PublishingSubmissionResult.Of(PublishingSubmissionOutcome.Unavailable, "provider_unavailable", "No.");

        public PublishingCancellationResult NextCancellation { get; set; } =
            new(PublishingCancellationOutcome.Cancelled);

        public PublishingPostLookupResult NextListing { get; set; } =
            new(PublishingPostLookupOutcome.Listed, [], Complete: true);

        public List<PublishingSubmission> Sent { get; } = [];

        public List<PublishingCancellation> Removals { get; } = [];

        public List<PublishingPostLookup> Listings { get; } = [];

        public PublishingProviderDescriptor Describe() =>
            new(true, PublishingPolicy.BufferProviderKey, workspaceId);

        public Task<PublishingSubmissionResult> SubmitAsync(
            PublishingSubmission submission, CancellationToken cancellationToken)
        {
            Sent.Add(submission);

            return Task.FromResult(Next);
        }

        public Task<PublishingCancellationResult> CancelAsync(
            PublishingCancellation cancellation, CancellationToken cancellationToken)
        {
            Removals.Add(cancellation);

            return Task.FromResult(NextCancellation);
        }

        public Task<PublishingPostLookupResult> ListPostsAsync(
            PublishingPostLookup lookup, CancellationToken cancellationToken)
        {
            Listings.Add(lookup);

            return Task.FromResult(NextListing);
        }
    }

    private sealed class NoMedia : IMediaAssetLookupFacade
    {
        public Task<bool> ExistsAsync(Guid mediaAssetId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Reconciliation does not look at pictures.");

        public Task<MediaAssetLinkTarget> ResolveLinkTargetAsync(
            Guid mediaAssetId, int? versionNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Reconciliation does not look at pictures.");

        public Task<MediaAssetDescription?> DescribeAsync(
            Guid mediaAssetId, int? versionNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Reconciliation does not look at pictures.");

        public Task<MediaPictureTarget?> ResolvePictureAsync(
            Guid mediaAssetId, int? versionNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Reconciliation does not look at pictures.");

        public Task<MediaPictureOpen> OpenPictureAsync(
            Guid mediaAssetId, int versionNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Reconciliation does not look at pictures.");
    }

    private sealed class ZonedBrand : IBrandProfileFacade
    {
        public Task<OperationResult<BrandProfileServiceModel>> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult<BrandProfileServiceModel>.Success(new BrandProfileServiceModel(
                Guid.NewGuid(), "The test kitchen", null, null, null, Zone, null, [], [], [], 1, Now, Now, "token")));

        public Task<CreatorPantry.Domain.Managers.Idempotency.IdempotentOutcome<BrandProfileServiceModel>> CreateAsync(
            string userId, CreateBrandProfileViewModel model, string? idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Reconciliation does not edit a brand profile.");

        public Task<CreatorPantry.Domain.Managers.Idempotency.IdempotentOutcome<BrandProfileServiceModel>> UpdateAsync(
            string userId, UpdateBrandProfileViewModel model, string? idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Reconciliation does not edit a brand profile.");
    }
}
