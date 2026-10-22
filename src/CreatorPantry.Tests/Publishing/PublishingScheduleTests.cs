using CreatorPantry.Domain.Managers.Limits;
using Microsoft.Extensions.Options;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
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
/// PUB-002's rules and storage against a real SQL Server: what is validated before anything is sent, what is
/// committed before the provider is called, what each answer makes of the record, and what a retry does.
/// </summary>
/// <remarks>
/// <para>
/// The seam below the facade is built by hand over the fixture's own <c>DbContext</c>, with a scripted
/// provider and a clock the test moves. The card lookup is the <em>real</em> Content facade, so the
/// two-workspace tests are proven by the query filter rather than by a stub agreeing with the test.
/// </para>
/// <para>
/// One thing is deliberately not covered here: the submit budget running out. An interactive confirmation can
/// only ever make attempt number one, so <see cref="PublishingLedgerPolicy.MaxSubmitAttempts"/> is reachable
/// only through the reconciliation worker, and it is that worker's tests (13.10c) that must spend it.
/// </para>
/// </remarks>
public sealed partial class PublishingScheduleTests(SqlServerRecipeFixture fixture) : IClassFixture<SqlServerRecipeFixture>
{
    private const string Zone = "America/Chicago";
    private static readonly Guid WsA = SqlServerRecipeFixture.WorkspaceA;
    private static readonly Guid WsB = SqlServerRecipeFixture.WorkspaceB;
    private static readonly DateTimeOffset Now = SqlServerRecipeFixture.Now;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- The happy path ----

    [Fact]
    public async Task A_confirmation_is_committed_then_submitted_and_the_providers_post_id_is_mapped()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_1") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var result = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id));

        Assert.True(result.Succeeded, result.Error?.Code);
        var record = result.Value!.Record;
        Assert.False(result.Value.Replayed);
        Assert.Equal(PublishingRecordStatus.Scheduled, record.Status);
        Assert.Equal(PublishingPlatform.Instagram, record.Platform);
        Assert.Equal("Soda bread, out of the oven at last.", record.Text);
        Assert.False(record.OutcomeUncertain);
        Assert.Null(record.FailureCategory);
        Assert.Equal(1, record.SubmitAttempts);
        Assert.NotNull(record.SubmittedAt);
        Assert.NotEmpty(record.ConcurrencyToken);

        // The provider was asked exactly once, with the channel id from the integration record.
        var sent = Assert.Single(provider.Sent);
        Assert.Equal(profile.ExternalId, sent.ChannelExternalId);
        Assert.Equal(record.ScheduledAt, sent.DueAt);

        // The schedule keeps all three facts: the instant, the workspace's zone, the local time it means.
        Assert.Equal(Zone, record.ScheduleTimeZoneId);
        Assert.Equal(record.ScheduledAt.UtcDateTime.AddHours(-5), record.ScheduledLocal);

        // The provider's id for the post is in the integration record and nowhere else.
        var reference = await InAsync(WsA, db => db.ExternalPublicationReferences.AsNoTracking()
            .SingleAsync(item => item.PublishingRecordId == record.Id, Ct));
        Assert.Equal("update_EXTERNAL_1", reference.ExternalUpdateId);
        Assert.Equal(PublishingPolicy.BufferProviderKey, reference.ProviderKey);

        // One attempt row, accepted, with a completion time.
        var attempt = await InAsync(WsA, db => db.PublishingDeliveryAttempts.AsNoTracking()
            .SingleAsync(item => item.PublishingRecordId == record.Id, Ct));
        Assert.Equal((1, PublishingOperation.Submit, PublishingAttemptOutcome.Accepted),
            (attempt.AttemptNumber, attempt.Operation, attempt.Outcome));
        Assert.NotNull(attempt.CompletedAt);
        Assert.Null(attempt.FailureCategory);
    }

    [Fact]
    public async Task Confirming_and_submitting_are_both_audited_and_neither_entry_carries_the_text()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_2") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var result = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id));
        var recordId = result.Value!.Record.Id;

        var entries = await InAsync(WsA, db => db.AuditLogs.AsNoTracking()
            .Where(log => log.ResourceId == recordId.ToString("D"))
            .OrderBy(log => log.OccurredAt)
            .ToListAsync(Ct));

        Assert.Equal(
            [PublishingAuditActions.Confirmed, PublishingAuditActions.Submitted],
            entries.Select(entry => entry.Action));

        // Ids, a network, a time and a state. Never what is being published.
        Assert.All(entries, entry =>
            Assert.DoesNotContain("Soda bread", entry.Summary, StringComparison.Ordinal));
        Assert.Contains($"status:{PublishingRecordStatus.Scheduled}", entries[^1].AfterReference);
    }

    // ---- A retry publishes nothing twice ----

    [Fact]
    public async Task The_same_key_returns_the_first_record_and_the_provider_is_not_asked_again()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_3") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);
        var confirmation = Confirmation(card.Id, profile.Id);
        var key = NewKey();

        var member = Guid.NewGuid();
        var first = await ScheduleAsync(WsA, provider, confirmation, key, membershipId: member);
        var replay = await ScheduleAsync(WsA, provider, confirmation, key, membershipId: member);

        Assert.True(first.Succeeded, first.Error?.Code);
        Assert.True(replay.Succeeded, replay.Error?.Code);
        Assert.True(replay.Value!.Replayed);
        Assert.Equal(first.Value!.Record.Id, replay.Value.Record.Id);

        // The guard that matters: one key, one post.
        Assert.Single(provider.Sent);

        Assert.Equal(1, await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .CountAsync(record => record.IdempotencyKey == key, Ct)));
    }

    [Fact]
    public async Task The_same_key_for_a_different_publication_is_refused_and_nothing_is_sent()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_4") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);
        var key = NewKey();

        await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), key);

        var second = Confirmation(card.Id, profile.Id);
        var reused = await ScheduleAsync(
            WsA,
            provider,
            new SchedulePublishingViewModel
            {
                ContentCardId = second.ContentCardId,
                PublishingProfileId = second.PublishingProfileId,
                Text = "Something else entirely.",
                ScheduledAt = second.ScheduledAt,
                Confirmed = true,
            },
            key);

        Assert.Equal(IdempotencyPolicy.KeyReusedCode, reused.Error!.Code);
        Assert.Single(provider.Sent);
    }

    [Fact]
    public async Task The_same_key_used_by_a_different_member_is_a_different_request()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_16") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);
        var confirmation = Confirmation(card.Id, profile.Id);
        var key = NewKey();

        await ScheduleAsync(WsA, provider, confirmation, key, membershipId: Guid.NewGuid());

        // A key names one person's request. Handing somebody else the first member's record would report a
        // publication they did not confirm — and the audit trail already names the first member as the actor.
        var other = await ScheduleAsync(WsA, provider, confirmation, key, membershipId: Guid.NewGuid());

        Assert.Equal(IdempotencyPolicy.KeyReusedCode, other.Error!.Code);
        Assert.Single(provider.Sent);
    }

    [Fact]
    public async Task Two_workspaces_may_confirm_under_the_same_key()
    {
        var key = NewKey();

        foreach (var workspaceId in new[] { WsA, WsB })
        {
            var provider = new ScriptedProvider(workspaceId) { Next = Accepted($"update_EXTERNAL_{workspaceId:N}"[..24]) };
            var card = await SeedCardAsync(workspaceId);
            var profile = await SeedProfileAsync(workspaceId, provider.Key);

            var result = await ScheduleAsync(workspaceId, provider, Confirmation(card.Id, profile.Id), key);

            Assert.True(result.Succeeded);
            Assert.False(result.Value!.Replayed);
        }
    }

    // ---- The day's confirmations (15.3) ----

    [Fact]
    public async Task A_workspace_that_has_confirmed_its_day_is_refused_before_anything_is_written_or_sent()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_D1") };
        var profile = await SeedProfileAsync(WsA, provider.Key);
        var limits = new LimitsOptions { PublicationsPerWorkspacePerDay = await ConfirmedTodayAsync(WsA) + 1 };

        var member = Guid.NewGuid();
        var firstCard = await SeedCardAsync(WsA);
        var key = NewKey();
        var first = await ScheduleAsync(
            WsA, provider, Confirmation(firstCard.Id, profile.Id), key, membershipId: member, limits: limits);
        Assert.True(first.Succeeded, first.Error?.Code);

        var secondCard = await SeedCardAsync(WsA);
        var refused = await ScheduleAsync(WsA, provider, Confirmation(secondCard.Id, profile.Id), limits: limits);

        Assert.Equal(LimitErrorCodes.BudgetExhausted, refused.Error!.Code);
        Assert.Equal("PublishingCommand", refused.Error.Extensions!["operation"]);
        Assert.Equal(LimitDay.NextAfter(new MovableClock().UtcNow), refused.Error.Extensions["resetsAt"]);
        Assert.Single(provider.Sent);
        Assert.Equal(0, await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .CountAsync(record => record.ContentCardId == secondCard.Id, Ct)));

        // A replay of the confirmation that was admitted is its answer, spent allowance or not.
        var replay = await ScheduleAsync(
            WsA, provider, Confirmation(firstCard.Id, profile.Id), key, membershipId: member, limits: limits);
        Assert.True(replay.Succeeded, replay.Error?.Code);
        Assert.True(replay.Value!.Replayed);
    }

    [Fact]
    public async Task One_workspaces_day_of_confirmations_is_not_anothers()
    {
        var providerA = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_D2") };
        var profileA = await SeedProfileAsync(WsA, providerA.Key);
        var providerB = new ScriptedProvider(WsB) { Next = Accepted("update_EXTERNAL_D3") };
        var profileB = await SeedProfileAsync(WsB, providerB.Key);

        // A's allowance is exactly what A has already used; B has room under the same number.
        var usedA = await ConfirmedTodayAsync(WsA);
        var limits = new LimitsOptions { PublicationsPerWorkspacePerDay = Math.Max(usedA, await ConfirmedTodayAsync(WsB) + 1) };

        while (await ConfirmedTodayAsync(WsA) < limits.PublicationsPerWorkspacePerDay)
        {
            providerA.Next = Accepted($"update_EXTERNAL_{Guid.NewGuid():N}"[..24]);
            var filler = await SeedCardAsync(WsA);
            Assert.True((await ScheduleAsync(WsA, providerA, Confirmation(filler.Id, profileA.Id), limits: limits)).Succeeded);
        }

        var cardA = await SeedCardAsync(WsA);
        Assert.Equal(
            LimitErrorCodes.BudgetExhausted,
            (await ScheduleAsync(WsA, providerA, Confirmation(cardA.Id, profileA.Id), limits: limits)).Error!.Code);

        var cardB = await SeedCardAsync(WsB);
        var admitted = await ScheduleAsync(WsB, providerB, Confirmation(cardB.Id, profileB.Id), limits: limits);
        Assert.True(admitted.Succeeded, admitted.Error?.Code);
    }

    // ---- A new key is not a second post either ----

    [Fact]
    public async Task The_same_publication_under_a_new_key_is_refused_while_the_first_is_still_going_out()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_17") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);
        var confirmation = Confirmation(card.Id, profile.Id);

        var first = await ScheduleAsync(WsA, provider, confirmation, NewKey());

        // The hole an idempotency key cannot close: two keys are two requests, so nothing about the key says
        // this is the same post. A second createPost here would be a duplicate nobody asked for.
        var again = await ScheduleAsync(WsA, provider, confirmation, NewKey());

        Assert.Equal(PublishingErrorCodes.DuplicatePublication, again.Error!.Code);
        Assert.Equal(first.Value!.Record.Id, Assert.IsType<Guid>(again.Error.Extensions!["publishingRecordId"]));
        Assert.Single(provider.Sent);
    }

    [Fact]
    public async Task A_publication_the_provider_refused_may_be_confirmed_again()
    {
        var provider = new ScriptedProvider(WsA)
        {
            Next = PublishingSubmissionResult.Of(
                PublishingSubmissionOutcome.Refused, "provider_rejected_input", "No."),
        };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);
        var confirmation = Confirmation(card.Id, profile.Id);

        var failed = await ScheduleAsync(WsA, provider, confirmation, NewKey());
        Assert.Equal(PublishingRecordStatus.Failed, failed.Value!.Record.Status);

        // The guard is about what is *going out*. A creator who fixed the problem may try the same post again.
        provider.Next = Accepted("update_EXTERNAL_18");
        var retry = await ScheduleAsync(WsA, provider, confirmation, NewKey());

        Assert.True(retry.Succeeded, retry.Error?.Code);
        Assert.Equal(PublishingRecordStatus.Scheduled, retry.Value!.Record.Status);
        Assert.NotEqual(failed.Value.Record.Id, retry.Value.Record.Id);
    }

    [Fact]
    public async Task Only_the_call_that_moves_a_record_into_submitting_may_talk_to_the_provider()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_19") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        // A record left Pending, as a throttle leaves one.
        provider.Next = PublishingSubmissionResult.Of(
            PublishingSubmissionOutcome.RateLimited, "rate_limited", "Throttled.");
        var pending = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey());
        var recordId = pending.Value!.Record.Id;
        Assert.Equal(PublishingRecordStatus.Pending, pending.Value.Record.Status);

        // Two claims on the same row: the second must not be given permission to call, whoever it is. This is
        // the gate the reconciliation worker will lean on, so it is asserted at the data layer where it lives.
        await using var scope = fixture.ScopeFor(WsA);
        var layer = DataLayer(scope, provider);

        var mine = await layer.MarkSubmittingAsync(recordId, Now, Now.AddMinutes(1), Ct);
        var theirs = await layer.MarkSubmittingAsync(recordId, Now, Now.AddMinutes(1), Ct);

        Assert.True(mine.Transitioned);
        Assert.Equal(PublishingRecordStatus.Submitting, mine.Row!.Status);
        Assert.False(theirs.Transitioned);
        Assert.Equal(PublishingRecordStatus.Submitting, theirs.Row!.Status);
    }

    [Fact]
    public async Task A_caller_that_gives_up_mid_call_still_leaves_the_attempt_on_the_record()
    {
        var provider = new ScriptedProvider(WsA) { Abandons = true };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey()));

        // The trail that matters most is the one for a call nobody saw the end of.
        var record = await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(item => item.ContentCardId == card.Id, Ct));

        Assert.Equal(PublishingRecordStatus.Submitting, record.Status);
        Assert.Equal(PublishingOperation.Submit, record.UncertainOperation);
        Assert.Equal("request_abandoned", record.FailureCategory);

        var attempt = await InAsync(WsA, db => db.PublishingDeliveryAttempts.AsNoTracking()
            .SingleAsync(item => item.PublishingRecordId == record.Id, Ct));
        Assert.Equal(PublishingAttemptOutcome.Unknown, attempt.Outcome);
        Assert.Null(attempt.CompletedAt);
    }

    // ---- What each provider answer makes of the record ----

    [Fact]
    public async Task A_provider_that_never_answers_leaves_the_record_saying_the_outcome_is_unknown()
    {
        var provider = new ScriptedProvider(WsA)
        {
            Next = PublishingSubmissionResult.Of(
                PublishingSubmissionOutcome.Unknown, "provider_timeout", "No answer."),
        };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var result = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey());
        var record = result.Value!.Record;

        // Not Failed and not Scheduled: a post may exist, and nothing here is allowed to guess which.
        Assert.Equal(PublishingRecordStatus.Submitting, record.Status);
        Assert.True(record.OutcomeUncertain);
        Assert.Equal("provider_timeout", record.FailureCategory);
        Assert.Null(record.SubmittedAt);
        Assert.Null(record.CompletedAt);

        var stored = await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(item => item.Id == record.Id, Ct));
        Assert.Equal(PublishingOperation.Submit, stored.UncertainOperation);
        Assert.NotNull(stored.UncertainSince);

        // No post id was learned, so no mapping record was written.
        Assert.False(await InAsync(WsA, db => db.ExternalPublicationReferences.AsNoTracking()
            .AnyAsync(item => item.PublishingRecordId == record.Id, Ct)));

        // An attempt that was never answered has no completion time.
        var attempt = await InAsync(WsA, db => db.PublishingDeliveryAttempts.AsNoTracking()
            .SingleAsync(item => item.PublishingRecordId == record.Id, Ct));
        Assert.Equal(PublishingAttemptOutcome.Unknown, attempt.Outcome);
        Assert.Null(attempt.CompletedAt);
    }

    [Fact]
    public async Task A_replay_of_an_uncertain_submit_never_asks_the_provider_again()
    {
        var provider = new ScriptedProvider(WsA)
        {
            Next = PublishingSubmissionResult.Of(PublishingSubmissionOutcome.Unknown, "provider_timeout", "No answer."),
        };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);
        var confirmation = Confirmation(card.Id, profile.Id);
        var key = NewKey();

        var member = Guid.NewGuid();
        var first = await ScheduleAsync(WsA, provider, confirmation, key, membershipId: member);
        var replay = await ScheduleAsync(WsA, provider, confirmation, key, membershipId: member);

        Assert.True(replay.Succeeded, replay.Error?.Code);

        // The one case where asking again could double-post. It does not happen.
        Assert.Single(provider.Sent);
        Assert.True(replay.Value!.Replayed);
        Assert.Equal(first.Value!.Record.Id, replay.Value.Record.Id);
        Assert.True(replay.Value.Record.OutcomeUncertain);
    }

    [Fact]
    public async Task A_refusal_fails_the_record_with_a_category_of_our_own()
    {
        var provider = new ScriptedProvider(WsA)
        {
            Next = PublishingSubmissionResult.Of(
                PublishingSubmissionOutcome.Refused, "provider_rejected_input", "The provider would not accept it."),
        };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var result = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey());
        var record = result.Value!.Record;

        // A provider's refusal is a state of a confirmed publication, not a failure of the command.
        Assert.True(result.Succeeded);
        Assert.Equal(PublishingRecordStatus.Failed, record.Status);
        Assert.Equal("provider_rejected_input", record.FailureCategory);
        Assert.False(record.OutcomeUncertain);
        Assert.NotNull(record.CompletedAt);
        Assert.Null(record.SubmittedAt);
    }

    [Fact]
    public async Task A_throttle_leaves_it_pending_and_leaves_the_provider_alone_for_as_long_as_it_asked()
    {
        var provider = new ScriptedProvider(WsA)
        {
            Next = PublishingSubmissionResult.Of(
                PublishingSubmissionOutcome.RateLimited, "rate_limited", "Throttled.", retryAfter: TimeSpan.FromMinutes(7)),
        };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);
        var clock = new MovableClock();

        var result = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey(), clock);
        var record = result.Value!.Record;

        Assert.Equal(PublishingRecordStatus.Pending, record.Status);
        Assert.False(record.OutcomeUncertain);
        Assert.Equal(1, record.SubmitAttempts);

        var stored = await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(item => item.Id == record.Id, Ct));
        Assert.Equal(clock.UtcNow + TimeSpan.FromMinutes(7), stored.AvailableAt);
    }

    [Fact]
    public async Task A_provider_that_failed_before_it_could_act_leaves_it_pending_without_uncertainty()
    {
        var provider = new ScriptedProvider(WsA)
        {
            Next = PublishingSubmissionResult.Of(
                PublishingSubmissionOutcome.Unavailable, "provider_unavailable", "Could not take it."),
        };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var result = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey());

        Assert.Equal(PublishingRecordStatus.Pending, result.Value!.Record.Status);
        Assert.False(result.Value.Record.OutcomeUncertain);
    }

    // ---- What is refused before anything is sent ----

    [Fact]
    public async Task A_workspace_with_no_provider_of_its_own_cannot_schedule_and_learns_nothing_about_one()
    {
        // The provider is configured, but for workspace A. B is answered exactly as a deployment with none.
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_5") };
        var card = await SeedCardAsync(WsB);
        var profile = await SeedProfileAsync(WsB, provider.Key);

        var result = await ScheduleAsync(WsB, provider, Confirmation(card.Id, profile.Id), NewKey());

        Assert.Equal(PublishingErrorCodes.ProviderNotConfigured, result.Error!.Code);
        Assert.Empty(provider.Sent);
    }

    [Fact]
    public async Task A_card_that_is_not_this_workspaces_is_refused_by_the_real_lookup()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_6") };
        var theirCard = await SeedCardAsync(WsB);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var result = await ScheduleAsync(WsA, provider, Confirmation(theirCard.Id, profile.Id), NewKey());

        Assert.Equal(PublishingErrorCodes.ScheduleInvalid, result.Error!.Code);
        Assert.Contains("contentCardId", result.Error.FieldErrors.Keys);
        Assert.Empty(provider.Sent);

        // And nothing was written: a refused confirmation leaves no ledger row behind.
        Assert.False(await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .AnyAsync(record => record.ContentCardId == theirCard.Id, Ct)));
    }

    [Fact]
    public async Task Another_workspaces_profile_is_refused()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_7") };
        var card = await SeedCardAsync(WsA);
        var theirProfile = await SeedProfileAsync(WsB, provider.Key);

        var result = await ScheduleAsync(WsA, provider, Confirmation(card.Id, theirProfile.Id), NewKey());

        Assert.Equal(PublishingErrorCodes.ScheduleInvalid, result.Error!.Code);
        Assert.Contains("publishingProfileId", result.Error.FieldErrors.Keys);
        Assert.Empty(provider.Sent);
    }

    [Theory]
    [InlineData(PublishingProfileStatus.NeedsReconnecting)]
    [InlineData(PublishingProfileStatus.Locked)]
    [InlineData(PublishingProfileStatus.Removed)]
    public async Task A_profile_the_provider_will_not_post_to_is_refused(PublishingProfileStatus status)
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_8") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key, status);

        var result = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey());

        Assert.Equal(PublishingErrorCodes.ProfileNotReady, result.Error!.Code);
        Assert.Empty(provider.Sent);
    }

    [Fact]
    public async Task A_caller_whose_profile_list_is_out_of_date_is_told_so_rather_than_published_for()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_9") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var confirmation = Confirmation(card.Id, profile.Id);
        var result = await ScheduleAsync(
            WsA,
            provider,
            new SchedulePublishingViewModel
            {
                ContentCardId = confirmation.ContentCardId,
                PublishingProfileId = confirmation.PublishingProfileId,
                ExpectedPlatform = PublishingPlatform.LinkedIn,
                Text = confirmation.Text,
                ScheduledAt = confirmation.ScheduledAt,
                Confirmed = true,
            },
            NewKey());

        Assert.Equal(PublishingErrorCodes.PlatformMismatch, result.Error!.Code);
        Assert.Empty(provider.Sent);
    }

    [Fact]
    public async Task A_picture_cannot_be_published_yet_and_is_refused_rather_than_dropped()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_10") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);
        var confirmation = Confirmation(card.Id, profile.Id);

        var result = await ScheduleAsync(
            WsA,
            provider,
            new SchedulePublishingViewModel
            {
                ContentCardId = confirmation.ContentCardId,
                PublishingProfileId = confirmation.PublishingProfileId,
                Text = confirmation.Text,
                MediaAssetId = Guid.NewGuid(),
                MediaAssetVersionNumber = 1,
                ScheduledAt = confirmation.ScheduledAt,
                Confirmed = true,
            },
            NewKey(),
            media: MediaAssetLinkTarget.Linkable);

        // The text is not quietly sent on its own: the creator believes a picture is going out.
        Assert.Equal(PublishingErrorCodes.MediaNotPublishable, result.Error!.Code);
        Assert.Empty(provider.Sent);
    }

    [Fact]
    public async Task A_picture_that_is_not_this_workspaces_is_the_other_refusal()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_11") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);
        var confirmation = Confirmation(card.Id, profile.Id);

        var result = await ScheduleAsync(
            WsA,
            provider,
            new SchedulePublishingViewModel
            {
                ContentCardId = confirmation.ContentCardId,
                PublishingProfileId = confirmation.PublishingProfileId,
                Text = confirmation.Text,
                MediaAssetId = Guid.NewGuid(),
                MediaAssetVersionNumber = 1,
                ScheduledAt = confirmation.ScheduledAt,
                Confirmed = true,
            },
            NewKey(),
            media: MediaAssetLinkTarget.AssetNotFound);

        // Told apart from "cannot be published yet": only one of the two is the creator's mistake.
        Assert.Equal(PublishingErrorCodes.ScheduleInvalid, result.Error!.Code);
        Assert.Contains("mediaAssetId", result.Error.FieldErrors.Keys);
    }

    [Fact]
    public async Task A_workspace_with_no_time_zone_is_asked_to_set_one_rather_than_having_one_guessed()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_12") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var result = await ScheduleAsync(
            WsA, provider, Confirmation(card.Id, profile.Id), NewKey(), zone: null);

        Assert.Equal(PublishingErrorCodes.TimeZoneUnset, result.Error!.Code);
        Assert.Empty(provider.Sent);
    }

    [Fact]
    public async Task A_time_in_the_past_or_too_close_to_now_is_refused()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_13") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        foreach (var when in new[] { Now.AddDays(-1), Now.AddMinutes(1) })
        {
            var result = await ScheduleAsync(
                WsA,
                provider,
                new SchedulePublishingViewModel
                {
                    ContentCardId = card.Id,
                    PublishingProfileId = profile.Id,
                    Text = "Soda bread, out of the oven at last.",
                    ScheduledAt = when,
                    Confirmed = true,
                },
                NewKey());

            Assert.Equal(PublishingErrorCodes.ScheduleInvalid, result.Error!.Code);
            Assert.Contains("scheduledAt", result.Error.FieldErrors.Keys);
        }

        Assert.Empty(provider.Sent);
    }

    [Fact]
    public async Task A_time_beyond_the_horizon_is_refused()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_14") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var result = await ScheduleAsync(
            WsA,
            provider,
            new SchedulePublishingViewModel
            {
                ContentCardId = card.Id,
                PublishingProfileId = profile.Id,
                Text = "Next year, maybe.",
                ScheduledAt = Now + PublishingLedgerPolicy.MaximumScheduleHorizon + TimeSpan.FromDays(1),
                Confirmed = true,
            },
            NewKey());

        Assert.Equal(PublishingErrorCodes.ScheduleInvalid, result.Error!.Code);
        Assert.Empty(provider.Sent);
    }

    // ---- Reading one back ----

    [Fact]
    public async Task A_record_is_readable_by_id_in_its_own_workspace_and_nowhere_else()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_15") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var scheduled = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey());
        var recordId = scheduled.Value!.Record.Id;

        var mine = await GetAsync(WsA, provider, recordId);
        Assert.True(mine.Succeeded);
        Assert.Equal(recordId, mine.Value!.Id);

        // The same answer a record that never existed gets, so asking cannot reveal a neighbour's.
        var theirs = await GetAsync(WsB, provider, recordId);
        Assert.Equal(PublishingErrorCodes.RecordNotFound, theirs.Error!.Code);
        Assert.Equal(
            PublishingErrorCodes.RecordNotFound,
            (await GetAsync(WsA, provider, Guid.NewGuid())).Error!.Code);
    }

    // ---- Helpers ----

    private static string NewKey() => $"confirm-{Guid.NewGuid():N}";

    private static PublishingSubmissionResult Accepted(string postId) =>
        new(PublishingSubmissionOutcome.Scheduled, postId, ProviderStatusCode: 200);

    private static SchedulePublishingViewModel Confirmation(Guid cardId, Guid profileId) => new()
    {
        ContentCardId = cardId,
        PublishingProfileId = profileId,
        Text = "Soda bread, out of the oven at last.",
        ScheduledAt = Now.AddDays(3),
        Confirmed = true,
    };

    private async Task<OperationResult<PublishingScheduleOutcome>> ScheduleAsync(
        Guid workspaceId,
        ScriptedProvider provider,
        SchedulePublishingViewModel model,
        string? idempotencyKey = null,
        MovableClock? clock = null,
        string? zone = Zone,
        MediaAssetLinkTarget media = MediaAssetLinkTarget.Linkable,
        Guid? membershipId = null,
        LimitsOptions? limits = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId, membershipId: membershipId);

        return await Business(scope, provider, clock ?? new MovableClock(), zone, media, limits)
            .ScheduleAsync("test-account", model, idempotencyKey ?? NewKey(), Ct);
    }

    /// <summary>What this workspace has already confirmed today: the fixture's other tests share both.</summary>
    private Task<int> ConfirmedTodayAsync(Guid workspaceId) =>
        InAsync(workspaceId, db => new PublishingRecordRepository(db)
            .CountConfirmedSinceAsync(LimitDay.StartOf(new MovableClock().UtcNow), Ct));

    private async Task<OperationResult<PublishingRecordServiceModel>> GetAsync(
        Guid workspaceId, ScriptedProvider provider, Guid recordId)
    {
        await using var scope = fixture.ScopeFor(workspaceId);

        return await Business(scope, provider, new MovableClock(), Zone, MediaAssetLinkTarget.Linkable)
            .GetAsync(recordId, Ct);
    }

    private static PublishingRecordDataLayer DataLayer(AsyncServiceScope scope, ScriptedProvider provider)
    {
        var db = SqlServerRecipeFixture.Db(scope);

        return new PublishingRecordDataLayer(
            new PublishingRecordRepository(db),
            provider,
            scope.ServiceProvider.GetRequiredService<IAuditWriter>(),
            db,
            NullLogger<PublishingRecordDataLayer>.Instance);
    }

    /// <summary>The seam below the facade, over this scope's own DbContext.</summary>
    private static PublishingRecordBusiness Business(
        AsyncServiceScope scope,
        ScriptedProvider provider,
        MovableClock clock,
        string? zone,
        MediaAssetLinkTarget media,
        LimitsOptions? limits = null)
    {
        var db = SqlServerRecipeFixture.Db(scope);

        return new PublishingRecordBusiness(
            new PublishingRecordDataLayer(
                new PublishingRecordRepository(db),
                provider,
                scope.ServiceProvider.GetRequiredService<IAuditWriter>(),
                db,
                NullLogger<PublishingRecordDataLayer>.Instance),

            // The real one: a card that is not this workspace's is refused by the query filter, not by a stub.
            scope.ServiceProvider.GetRequiredService<IContentCardLookupFacade>(),
            new ScriptedMedia(media),
            new ScriptedBrand(zone),
            Zones,
            scope.ServiceProvider.GetRequiredService<IWorkspaceContext>(),
            clock,
            Options.Create(limits ?? new LimitsOptions()));
    }

    private static readonly ITimeZoneConverter Zones =
        new ServiceCollection().AddApplicationTime().BuildServiceProvider().GetRequiredService<ITimeZoneConverter>();

    private async Task<ContentCard> SeedCardAsync(Guid workspaceId)
    {
        var card = new ContentCard
        {
            Id = Guid.NewGuid(),
            Title = "Soda bread, Friday",
            Column = ContentCardColumn.Approved,
            SortRank = ContentCardPolicy.RankGap,
            CreatedByMembershipId = SqlServerRecipeFixture.AuthorOne,
            UpdatedByMembershipId = SqlServerRecipeFixture.AuthorOne,
            CreatedAt = Now,
            UpdatedAt = Now,
        };

        await InAsync(workspaceId, async db =>
        {
            db.ContentCards.Add(card);
            await db.SaveChangesAsync(Ct);
        });

        return card;
    }

    /// <summary>A profile and the provider's own id for it, unique per seed: the fixture is shared and the
    /// mapping is unique per workspace, provider and external id.</summary>
    private async Task<SeededProfile> SeedProfileAsync(
        Guid workspaceId, string providerKey, PublishingProfileStatus status = PublishingProfileStatus.Ready)
    {
        var externalId = $"ch_EXTERNAL_{Guid.NewGuid():N}";

        var profile = new PublishingProfile
        {
            Id = Guid.NewGuid(),
            ProviderKey = providerKey,
            Platform = PublishingPlatform.Instagram,
            DisplayName = "The test kitchen",
            TimeZoneId = Zone,
            Status = status,
            FirstSeenAt = Now,
            LastSeenAt = Now,
        };

        await InAsync(workspaceId, async db =>
        {
            db.PublishingProfiles.Add(profile);
            db.ExternalPublishingProfileReferences.Add(new ExternalPublishingProfileReference
            {
                Id = Guid.NewGuid(),
                PublishingProfileId = profile.Id,
                ProviderKey = providerKey,
                ExternalId = externalId,
                ExternalAccountId = "org_EXTERNAL_1",
            });
            await db.SaveChangesAsync(Ct);
        });

        return new SeededProfile(profile.Id, externalId);
    }

    private sealed record SeededProfile(Guid Id, string ExternalId);

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

    private sealed class MovableClock : IClock
    {
        private long _ticks = Now.UtcTicks;

        public DateTimeOffset UtcNow => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>A provider that answers what the test says, under a key of its own: the fixture is shared.</summary>
    private sealed class ScriptedProvider(Guid? workspaceId) : IPublishingPostGateway
    {
        public string Key { get; } = PublishingPolicy.BufferProviderKey;

        public PublishingSubmissionResult Next { get; set; } =
            PublishingSubmissionResult.Of(PublishingSubmissionOutcome.NotConfigured);

        /// <summary>Guarded: the race tests call this from two scopes at once.</summary>
        public List<PublishingSubmission> Sent { get; } = [];

        /// <summary>The caller gave up while the provider was being asked.</summary>
        public bool Abandons { get; set; }

        public PublishingProviderDescriptor Describe() => new(true, Key, workspaceId);

        public PublishingCancellationResult NextCancellation { get; set; } =
            new(PublishingCancellationOutcome.Cancelled);

        public List<PublishingCancellation> Removals { get; } = [];

        public Task<PublishingSubmissionResult> SubmitAsync(
            PublishingSubmission submission, CancellationToken cancellationToken)
        {
            lock (Sent)
            {
                Sent.Add(submission);
            }

            return Abandons
                ? throw new OperationCanceledException("The caller stopped waiting.")
                : Task.FromResult(Next);
        }

        public Task<PublishingCancellationResult> CancelAsync(
            PublishingCancellation cancellation, CancellationToken cancellationToken)
        {
            Removals.Add(cancellation);

            return Abandons
                ? throw new OperationCanceledException("The caller stopped waiting.")
                : Task.FromResult(NextCancellation);
        }

        public PublishingPostLookupResult NextListing { get; set; } =
            new(PublishingPostLookupOutcome.Listed, [], Complete: true);

        public List<PublishingPostLookup> Listings { get; } = [];

        public Task<PublishingPostLookupResult> ListPostsAsync(
            PublishingPostLookup lookup, CancellationToken cancellationToken)
        {
            Listings.Add(lookup);

            return Task.FromResult(NextListing);
        }
    }

    private sealed class ScriptedMedia(MediaAssetLinkTarget target) : IMediaAssetLookupFacade
    {
        public Task<bool> ExistsAsync(Guid mediaAssetId, CancellationToken cancellationToken) =>
            Task.FromResult(target is MediaAssetLinkTarget.Linkable);

        public Task<MediaAssetLinkTarget> ResolveLinkTargetAsync(
            Guid mediaAssetId, int? versionNumber, CancellationToken cancellationToken) =>
            Task.FromResult(target);

        public Task<MediaAssetDescription?> DescribeAsync(
            Guid mediaAssetId, int? versionNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Scheduling does not describe a picture.");

        public Task<MediaPictureTarget?> ResolvePictureAsync(
            Guid mediaAssetId, int? versionNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Scheduling does not read a picture.");

        public Task<MediaPictureOpen> OpenPictureAsync(
            Guid mediaAssetId, int versionNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Scheduling never opens bytes: the provider fetches by address.");
    }

    private sealed class ScriptedBrand(string? zone) : IBrandProfileFacade
    {
        public Task<OperationResult<BrandProfileServiceModel>> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult<BrandProfileServiceModel>.Success(new BrandProfileServiceModel(
                Guid.NewGuid(),
                "The test kitchen",
                null,
                null,
                null,
                zone,
                null,
                [],
                [],
                [],
                1,
                Now,
                Now,
                "token")));

        public Task<IdempotentOutcome<BrandProfileServiceModel>> CreateAsync(
            string userId, CreateBrandProfileViewModel model, string? idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Scheduling does not create a brand profile.");

        public Task<IdempotentOutcome<BrandProfileServiceModel>> UpdateAsync(
            string userId, UpdateBrandProfileViewModel model, string? idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Scheduling does not edit a brand profile.");
    }
}
