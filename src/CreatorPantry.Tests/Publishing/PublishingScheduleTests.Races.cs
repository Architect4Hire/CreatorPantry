using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Publishing.Data;
using CreatorPantry.Domain.Modules.Publishing.Gateways;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using CreatorPantry.Tests.Recipes;
using LinkTarget = CreatorPantry.Domain.Modules.Media.Managers.MediaAssetLinkTarget;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CreatorPantry.Tests.Publishing;

/// <summary>
/// What two callers at once can and cannot do (13.10e): the properties that only two real database
/// connections can demonstrate.
/// </summary>
/// <remarks>
/// Every other race test in this folder runs on one <c>DbContext</c>, where the identity map answers before
/// the database does. These take a scope each, so the row version and the unique indexes are what settle it —
/// which is the only way to know they do.
/// </remarks>
public sealed partial class PublishingScheduleTests
{
    [Fact]
    public async Task Two_confirmations_of_the_same_publication_at_once_produce_one_post()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_RACE") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);
        var confirmation = Confirmation(card.Id, profile.Id);

        // Two tabs, two keys, the same publication. The idempotency key cannot see this: two keys are two
        // requests. The unique index is what makes the second row impossible.
        var first = ScheduleAsync(WsA, provider, confirmation, NewKey());
        var second = ScheduleAsync(WsA, provider, confirmation, NewKey());

        var outcomes = await Task.WhenAll(first, second);

        Assert.Single(provider.Sent);

        var live = await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .Where(record => record.ContentCardId == card.Id)
            .ToListAsync(Ct));
        Assert.Single(live);

        // One of them is told what is already going out; neither is told it published something it did not.
        Assert.Contains(outcomes, outcome => outcome.Succeeded);
        var refused = Assert.Single(outcomes.Where(outcome => !outcome.Succeeded));
        Assert.Equal(PublishingErrorCodes.DuplicatePublication, refused.Error!.Code);
    }

    [Fact]
    public async Task Two_confirmations_sharing_a_key_at_once_produce_one_post()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_RACE2") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);
        var confirmation = Confirmation(card.Id, profile.Id);
        var key = NewKey();
        var member = Guid.NewGuid();

        var outcomes = await Task.WhenAll(
            ScheduleAsync(WsA, provider, confirmation, key, membershipId: member),
            ScheduleAsync(WsA, provider, confirmation, key, membershipId: member));

        Assert.Single(provider.Sent);
        Assert.All(outcomes, outcome => Assert.True(outcome.Succeeded, outcome.Error?.Code));

        // Both are answered with the one record, and one of them says so.
        Assert.Single(outcomes.Select(outcome => outcome.Value!.Record.Id).Distinct());
        Assert.Contains(outcomes, outcome => outcome.Value!.Replayed);
    }

    [Fact]
    public async Task Two_workers_claiming_one_record_at_once_leave_exactly_one_holding_it()
    {
        var provider = new ScriptedProvider(WsA)
        {
            Next = PublishingSubmissionResult.Of(PublishingSubmissionOutcome.RateLimited, "rate_limited", "Throttled."),
        };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var pending = await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey());
        var recordId = pending.Value!.Record.Id;

        // A scope each, so the row version settles it rather than a shared change tracker.
        await using var mine = fixture.ScopeFor(WsA);
        await using var theirs = fixture.ScopeFor(WsA);

        var results = await Task.WhenAll(
            DataLayer(mine, provider).MarkSubmittingAsync(recordId, Now, Now.AddMinutes(1), Ct),
            DataLayer(theirs, provider).MarkSubmittingAsync(recordId, Now, Now.AddMinutes(1), Ct));

        // Exactly one may call the provider, and losing is an ordinary answer rather than an exception.
        Assert.Single(results.Where(result => result.Transitioned));
        Assert.All(results, result => Assert.NotNull(result.Row));

        var stored = await InAsync(WsA, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(record => record.Id == recordId, Ct));
        Assert.Equal(PublishingRecordStatus.Submitting, stored.Status);

        // One attempt from the throttled confirmation that made this row, and exactly one from the claim that
        // won — not two. The loser's increment is rolled back with its write.
        Assert.Equal(2, stored.SubmitAttempts);
    }

    [Fact]
    public async Task A_record_already_being_submitted_is_never_submitted_again_by_the_worker()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_RACE3") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        // A row in flight: the state a crashed request, or another worker, leaves behind.
        var recordId = await SeedSubmittingAsync(WsA, card.Id, profile.Id);

        await using var scope = fixture.ScopeFor(WsA);
        var target = await DataLayer(scope, provider).FindReconcileTargetAsync(recordId, Ct);

        var status = await Business(scope, provider, new MovableClock(), Zone, LinkTarget.Linkable)
            .SubmitPendingAsync(target!, PublishingPolicy.BufferProviderKey, Ct);

        // The gate is the permission to call, and this call does not have it.
        Assert.Empty(provider.Sent);
        Assert.Equal(PublishingRecordStatus.Submitting, status);
    }

    private async Task<Guid> SeedSubmittingAsync(Guid workspaceId, Guid cardId, Guid profileId)
    {
        var id = Guid.NewGuid();

        await InAsync(workspaceId, async db =>
        {
            db.PublishingRecords.Add(new Domain.Modules.Publishing.Data.Entities.PublishingRecord
            {
                Id = id,
                ContentCardId = cardId,
                PublishingProfileId = profileId,
                Platform = PublishingPlatform.Instagram,
                ContentText = "Soda bread, out of the oven at last.",
                ScheduledAt = Now.AddDays(3),
                ScheduleTimeZoneId = Zone,
                ScheduledLocal = Now.AddDays(3).UtcDateTime,
                Status = PublishingRecordStatus.Submitting,
                StatusChangedAt = Now,
                AvailableAt = Now,
                UncertainOperation = PublishingOperation.Submit,
                UncertainSince = Now,
                SubmitAttempts = 1,
                ConfirmedByMembershipId = SqlServerRecipeFixture.AuthorOne,
                CreatedAt = Now,
                IdempotencyKey = $"seeded-{Guid.NewGuid():N}",
            });

            await db.SaveChangesAsync(Ct);
        });

        return id;
    }
}
