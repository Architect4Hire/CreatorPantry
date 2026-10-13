using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Publishing.Gateways;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Tests.Publishing;

/// <summary>
/// PUB-003 against the same real SQL Server: what a cancellation asks of the provider, what it refuses to ask,
/// and the one thing it will not record.
/// </summary>
/// <remarks>
/// The rule these tests exist to hold: a publication is recorded as cancelled only when the provider removed
/// its post or never had one. Every other answer leaves it going out, still trying, or unknown.
/// </remarks>
public sealed partial class PublishingScheduleTests
{
    // ---- Nothing was ever sent ----

    [Fact]
    public async Task A_publication_that_was_never_sent_is_stopped_here_with_nothing_asked_of_the_provider()
    {
        var provider = new ScriptedProvider(WsA)
        {
            // Throttled: the record stays Pending, and no post exists anywhere.
            Next = PublishingSubmissionResult.Of(PublishingSubmissionOutcome.RateLimited, "rate_limited", "Throttled."),
        };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var scheduled = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey());
        Assert.Equal(PublishingRecordStatus.Pending, scheduled.Value!.Record.Status);

        var cancelled = await CancelAsync(WsA, provider, scheduled.Value.Record);

        Assert.True(cancelled.Succeeded, cancelled.Error?.Code);
        Assert.Equal(PublishingRecordStatus.Cancelled, cancelled.Value!.Status);
        Assert.NotNull(cancelled.Value.CompletedAt);
        Assert.False(cancelled.Value.OutcomeUncertain);

        // There was no post to remove, so the provider was not troubled about it.
        Assert.Empty(provider.Removals);

        // And no attempt row claims a call that never happened: the submit's is the only one.
        var attempts = await InAsync(WsA, db => db.PublishingDeliveryAttempts.AsNoTracking()
            .Where(attempt => attempt.PublishingRecordId == cancelled.Value.Id)
            .Select(attempt => attempt.Operation)
            .ToListAsync(Ct));
        Assert.Equal([PublishingOperation.Submit], attempts);
    }

    // ---- The post is at the provider ----

    [Fact]
    public async Task A_scheduled_publication_is_removed_at_the_provider_and_audited_twice()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_C1") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var scheduled = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey());
        var cancelled = await CancelAsync(WsA, provider, scheduled.Value!.Record);

        Assert.Equal(PublishingRecordStatus.Cancelled, cancelled.Value!.Status);
        Assert.NotNull(cancelled.Value.CompletedAt);
        Assert.Null(cancelled.Value.FailureCategory);

        // Asked by the provider's own id for the post, which came from the integration record.
        Assert.Equal("update_EXTERNAL_C1", Assert.Single(provider.Removals).ExternalUpdateId);

        var attempt = await InAsync(WsA, db => db.PublishingDeliveryAttempts.AsNoTracking()
            .SingleAsync(item => item.PublishingRecordId == cancelled.Value.Id
                && item.Operation == PublishingOperation.Cancel, Ct));
        Assert.Equal(PublishingAttemptOutcome.Accepted, attempt.Outcome);
        Assert.NotNull(attempt.CompletedAt);

        // The asking and the outcome are separate entries: the request happened whatever came of it.
        var actions = await InAsync(WsA, db => db.AuditLogs.AsNoTracking()
            .Where(log => log.ResourceId == cancelled.Value.Id.ToString("D"))
            .OrderBy(log => log.OccurredAt)
            .Select(log => log.Action)
            .ToListAsync(Ct));
        Assert.Equal(
            [
                PublishingAuditActions.Confirmed,
                PublishingAuditActions.Submitted,
                PublishingAuditActions.CancellationRequested,
                PublishingAuditActions.Cancelled,
            ],
            actions);
    }

    [Fact]
    public async Task A_post_the_provider_no_longer_holds_is_cancelled_too()
    {
        var provider = new ScriptedProvider(WsA)
        {
            Next = Accepted("update_EXTERNAL_C2"),
            NextCancellation = new PublishingCancellationResult(
                PublishingCancellationOutcome.AlreadyGone, FailureCategory: "provider_post_absent"),
        };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var scheduled = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey());
        var cancelled = await CancelAsync(WsA, provider, scheduled.Value!.Record);

        // Not being there is what was asked for.
        Assert.Equal(PublishingRecordStatus.Cancelled, cancelled.Value!.Status);
        Assert.Equal("provider_post_absent", cancelled.Value.FailureCategory);
    }

    [Fact]
    public async Task Cancelling_again_is_quiet_and_asks_the_provider_nothing()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_C3") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var scheduled = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey());
        var first = await CancelAsync(WsA, provider, scheduled.Value!.Record);
        var again = await CancelAsync(WsA, provider, first.Value!);

        // A creator may reasonably press it twice. The second is a success, not an error.
        Assert.True(again.Succeeded, again.Error?.Code);
        Assert.Equal(PublishingRecordStatus.Cancelled, again.Value!.Status);
        Assert.Single(provider.Removals);
    }

    [Fact]
    public async Task Cancelling_again_with_the_token_you_were_given_first_still_answers_cancelled()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_C3b") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var scheduled = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey());
        var asRead = scheduled.Value!.Record;

        await CancelAsync(WsA, provider, asRead);

        // The retry a creator actually makes after a lost answer: the same request, with the token they were
        // given. The first cancellation has moved the row version on, so quoting it is the normal case rather
        // than a stale read — and "repeating a cancellation is safe" has to be true of exactly this.
        var again = await CancelAsync(WsA, provider, asRead);

        Assert.True(again.Succeeded, again.Error?.Code);
        Assert.Equal(PublishingRecordStatus.Cancelled, again.Value!.Status);
        Assert.Single(provider.Removals);
    }

    // ---- The provider did not settle it ----

    [Fact]
    public async Task A_removal_nobody_got_an_answer_to_is_not_cancelled_and_settles_on_the_next_ask()
    {
        var provider = new ScriptedProvider(WsA)
        {
            Next = Accepted("update_EXTERNAL_C4"),
            NextCancellation = new PublishingCancellationResult(
                PublishingCancellationOutcome.Unknown, FailureCategory: "provider_timeout"),
        };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var scheduled = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey());
        var unsettled = await CancelAsync(WsA, provider, scheduled.Value!.Record);

        // The restriction, held by the state machine: an unknown removal is never a cancelled publication.
        Assert.Equal(PublishingRecordStatus.Cancelling, unsettled.Value!.Status);
        Assert.True(unsettled.Value.OutcomeUncertain);
        Assert.Null(unsettled.Value.CompletedAt);

        var stored = await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(item => item.Id == unsettled.Value.Id, Ct));
        Assert.Equal(PublishingOperation.Cancel, stored.UncertainOperation);

        // And this is why repeating a cancellation is the remedy rather than a risk: the provider says the
        // post is not there, and the record can finally say so too.
        provider.NextCancellation = new PublishingCancellationResult(
            PublishingCancellationOutcome.AlreadyGone, FailureCategory: "provider_post_absent");

        var settled = await CancelAsync(WsA, provider, unsettled.Value);

        Assert.Equal(PublishingRecordStatus.Cancelled, settled.Value!.Status);
        Assert.False(settled.Value.OutcomeUncertain);
        Assert.Equal(2, provider.Removals.Count);
    }

    [Fact]
    public async Task A_post_the_provider_will_not_remove_is_left_going_out()
    {
        var provider = new ScriptedProvider(WsA)
        {
            Next = Accepted("update_EXTERNAL_C5"),
            NextCancellation = new PublishingCancellationResult(
                PublishingCancellationOutcome.NotDeletable, FailureCategory: "provider_post_not_deletable"),
        };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var scheduled = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey());
        var refused = await CancelAsync(WsA, provider, scheduled.Value!.Record);

        // The post stands, so the record says what is true rather than what was wanted.
        Assert.True(refused.Succeeded, refused.Error?.Code);
        Assert.Equal(PublishingRecordStatus.Scheduled, refused.Value!.Status);
        Assert.Equal("provider_post_not_deletable", refused.Value.FailureCategory);
        Assert.False(refused.Value.OutcomeUncertain);
        Assert.Null(refused.Value.CompletedAt);
    }

    [Fact]
    public async Task A_credential_the_provider_refuses_cannot_remove_anything()
    {
        var provider = new ScriptedProvider(WsA)
        {
            Next = Accepted("update_EXTERNAL_C6"),
            NextCancellation = new PublishingCancellationResult(
                PublishingCancellationOutcome.Refused, FailureCategory: "credential_rejected"),
        };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var scheduled = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey());
        var refused = await CancelAsync(WsA, provider, scheduled.Value!.Record);

        Assert.Equal(PublishingRecordStatus.Scheduled, refused.Value!.Status);
        Assert.Equal("credential_rejected", refused.Value.FailureCategory);
    }

    // ---- What may not be cancelled ----

    [Fact]
    public async Task A_publication_whose_send_outcome_is_unknown_cannot_be_cancelled()
    {
        var provider = new ScriptedProvider(WsA)
        {
            Next = PublishingSubmissionResult.Of(
                PublishingSubmissionOutcome.Unknown, "provider_timeout", "No answer."),
        };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var unsure = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey());
        Assert.True(unsure.Value!.Record.OutcomeUncertain);

        var refused = await CancelAsync(WsA, provider, unsure.Value.Record);

        // There is no post this can name, so there is nothing to ask the provider to remove — and saying
        // "cancelled" would be a claim about a post that may well be going out.
        Assert.Equal(PublishingErrorCodes.OutcomeUnknown, refused.Error!.Code);
        Assert.Empty(provider.Removals);
    }

    [Fact]
    public async Task A_publication_that_already_went_out_cannot_be_cancelled()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_C7") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var scheduled = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey());
        var recordId = scheduled.Value!.Record.Id;

        // No route reaches Published yet — the reconciliation worker will — so the state is set directly.
        await InAsync(WsA, async db =>
        {
            var row = await db.PublishingRecords.SingleAsync(item => item.Id == recordId, Ct);
            row.Status = PublishingRecordStatus.Published;
            row.CompletedAt = Now.AddDays(3);
            await db.SaveChangesAsync(Ct);
        });

        var published = await GetAsync(WsA, provider, recordId);
        var refused = await CancelAsync(WsA, provider, published.Value!);

        // Taking something down again is a separate capability; promising it here would be a lie.
        Assert.Equal(PublishingErrorCodes.AlreadyPublished, refused.Error!.Code);
        Assert.Empty(provider.Removals);
    }

    [Fact]
    public async Task A_failed_publication_has_nothing_to_cancel()
    {
        var provider = new ScriptedProvider(WsA)
        {
            Next = PublishingSubmissionResult.Of(
                PublishingSubmissionOutcome.Refused, "provider_rejected_input", "No."),
        };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var failed = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey());
        var refused = await CancelAsync(WsA, provider, failed.Value!.Record);

        Assert.Equal(PublishingErrorCodes.NotCancellable, refused.Error!.Code);
        Assert.Empty(provider.Removals);
    }

    [Fact]
    public async Task A_token_from_before_the_record_moved_is_refused()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_C8") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var scheduled = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey());
        var stale = scheduled.Value!.Record;

        // Something moved the record after the caller read it — the race this guards is a post going out
        // between a screen being drawn and Cancel being pressed.
        await InAsync(WsA, async db =>
        {
            var row = await db.PublishingRecords.SingleAsync(item => item.Id == stale.Id, Ct);
            row.StatusChangedAt = Now.AddMinutes(1);
            await db.SaveChangesAsync(Ct);
        });

        var refused = await CancelAsync(WsA, provider, stale);

        Assert.Equal(PublishingErrorCodes.RecordConflict, refused.Error!.Code);
        Assert.Empty(provider.Removals);
    }

    [Fact]
    public async Task A_cancellation_needs_a_token_this_api_issued()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_C9") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var scheduled = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey());

        // The id overload, so a null token stays null rather than falling back to the record's own.
        foreach (var token in new[] { null, string.Empty, "   ", "not-a-token" })
        {
            var refused = await CancelAsync(WsA, provider, scheduled.Value!.Record.Id, token);

            Assert.False(refused.Succeeded);
            Assert.Equal(PublishingErrorCodes.RecordConflict, refused.Error!.Code);
            Assert.Empty(provider.Removals);
        }
    }

    // ---- Two workspaces ----

    [Fact]
    public async Task Another_workspaces_publication_cannot_be_cancelled_from_here()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_C10") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var mine = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey());

        var reaching = await CancelAsync(WsB, provider, mine.Value!.Record);

        // The same answer an id nobody has gets, so asking cannot reveal that the record exists elsewhere.
        Assert.Equal(PublishingErrorCodes.RecordNotFound, reaching.Error!.Code);
        Assert.Empty(provider.Removals);

        // And it is untouched: still going out, exactly as its own workspace left it.
        var untouched = await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(item => item.Id == mine.Value.Record.Id, Ct));
        Assert.Equal(PublishingRecordStatus.Scheduled, untouched.Status);
    }

    // ---- Helpers ----

    private Task<OperationResult<PublishingRecordServiceModel>> CancelAsync(
        Guid workspaceId, ScriptedProvider provider, PublishingRecordServiceModel record, string? token = null) =>
        CancelAsync(workspaceId, provider, record.Id, token ?? record.ConcurrencyToken);

    private async Task<OperationResult<PublishingRecordServiceModel>> CancelAsync(
        Guid workspaceId, ScriptedProvider provider, Guid recordId, string? token)
    {
        await using var scope = fixture.ScopeFor(workspaceId);

        return await Business(scope, provider, new MovableClock(), Zone, MediaAssetLinkTarget.Linkable)
            .CancelAsync(
                "test-account",
                recordId,
                new CancelPublishingViewModel { ExpectedConcurrencyToken = token },
                Ct);
    }
}
