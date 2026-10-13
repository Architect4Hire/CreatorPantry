using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Publishing.Data.Entities;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Tests.Media;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Tests.Publishing;

/// <summary>
/// 13.10 against a real SQL Server: the publishing ledger, its external publication references and its
/// delivery attempts, as the migration built them.
/// </summary>
/// <remarks>
/// <para>
/// There is no write seam yet — scheduling arrives with 13.10a — so these tests write rows directly and
/// assert what the model and the engine refuse. SQL Server rather than SQLite because the migration is what
/// is under test: three tables reaching one workspace by a single cascade path each, restricted keys into two
/// other modules' tables and one of this module's own, a filtered claim index, and a row version that has to
/// move.
/// </para>
/// <para>
/// The fixture is shared, so every assertion is scoped to rows the test made.
/// </para>
/// </remarks>
public sealed class PublishingLedgerTests(SqlServerRecipeFixture fixture) : IClassFixture<SqlServerRecipeFixture>
{
    private static readonly Guid WsA = SqlServerRecipeFixture.WorkspaceA;
    private static readonly Guid WsB = SqlServerRecipeFixture.WorkspaceB;
    private static readonly DateTimeOffset Now = SqlServerRecipeFixture.Now;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- The shape ----

    [Fact]
    public async Task A_record_round_trips_with_everything_it_was_confirmed_with()
    {
        var card = await SeedCardAsync(WsA);
        var profileId = await SeedProfileAsync(WsA);
        var (assetId, versionNumber) = await SeedAssetVersionAsync(WsA);

        var record = NewRecord(card.Id, profileId);
        record.MediaAssetId = assetId;
        record.MediaAssetVersionNumber = versionNumber;
        record.ScheduledLocal = new DateTime(2026, 10, 16, 8, 30, 0, DateTimeKind.Unspecified);
        record.ScheduleTimeZoneId = "America/Chicago";

        await InAsync(WsA, async db =>
        {
            db.PublishingRecords.Add(record);
            await db.SaveChangesAsync(Ct);
        });

        var loaded = await InAsync(
            WsA, db => db.PublishingRecords.AsNoTracking().SingleAsync(item => item.Id == record.Id, Ct));

        // Stamped by the ownership interceptor from the resolved context, never set by hand.
        Assert.Equal(WsA, loaded.WorkspaceId);
        Assert.Equal(card.Id, loaded.ContentCardId);
        Assert.Equal(profileId, loaded.PublishingProfileId);
        Assert.Equal(PublishingPlatform.Instagram, loaded.Platform);
        Assert.Equal("Soda bread, out of the oven at last.", loaded.ContentText);
        Assert.Equal((assetId, versionNumber), (loaded.MediaAssetId, loaded.MediaAssetVersionNumber));

        // The three separable facts about a schedule: the instant, the zone the creator chose it in, and the
        // wall-clock time they actually asked for (publishing.md).
        Assert.Equal(record.ScheduledAt, loaded.ScheduledAt);
        Assert.Equal("America/Chicago", loaded.ScheduleTimeZoneId);
        Assert.Equal(new DateTime(2026, 10, 16, 8, 30, 0, DateTimeKind.Unspecified), loaded.ScheduledLocal);

        Assert.Equal(PublishingRecordStatus.Pending, loaded.Status);
        Assert.Equal(0, loaded.SubmitAttempts);
        Assert.Null(loaded.UncertainOperation);
        Assert.Null(loaded.SubmittedAt);

        // The concurrency token 13.10a and 13.10b will quote.
        Assert.NotEmpty(loaded.RowVersion);
    }

    [Fact]
    public async Task A_record_needs_a_card_a_profile_a_time_and_the_text()
    {
        var card = await SeedCardAsync(WsA);
        var record = NewRecord(card.Id, await SeedProfileAsync(WsA));

        await InAsync(WsA, async db =>
        {
            db.PublishingRecords.Add(record);
            await db.SaveChangesAsync(Ct);
        });

        var loaded = await InAsync(
            WsA, db => db.PublishingRecords.AsNoTracking().SingleAsync(item => item.Id == record.Id, Ct));

        Assert.Null(loaded.MediaAssetId);
        Assert.Null(loaded.LastAttemptAt);
        Assert.Null(loaded.LeasedBy);
        Assert.Null(loaded.FailureCategory);
        Assert.Null(loaded.CompletedAt);
    }

    [Fact]
    public async Task An_edit_moves_the_row_version()
    {
        var record = await SeedRecordAsync(WsA);

        var before = record.RowVersion;

        await InAsync(WsA, async db =>
        {
            var tracked = await db.PublishingRecords.SingleAsync(item => item.Id == record.Id, Ct);
            tracked.Status = PublishingRecordStatus.Submitting;
            tracked.StatusChangedAt = Now.AddMinutes(1);
            await db.SaveChangesAsync(Ct);

            Assert.NotEqual(before, tracked.RowVersion);
        });
    }

    // ---- What the store refuses ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_record_may_not_carry_blank_content(string text)
    {
        var record = NewRecord(null, await SeedProfileAsync(WsA));
        record.ContentText = text;

        await AssertEngineRefusesAsync(WsA, record);
    }

    [Fact]
    public async Task A_record_must_say_what_state_it_is_in()
    {
        var record = NewRecord(null, await SeedProfileAsync(WsA));
        record.Status = default;

        await AssertEngineRefusesAsync(WsA, record);
    }

    [Fact]
    public async Task A_record_must_say_which_network_it_went_to()
    {
        var record = NewRecord(null, await SeedProfileAsync(WsA));
        record.Platform = default;

        await AssertEngineRefusesAsync(WsA, record);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_schedule_without_a_zone_is_refused(string zone)
    {
        // A schedule whose zone was guessed cannot be shown back to the creator as what they asked for.
        var record = NewRecord(null, await SeedProfileAsync(WsA));
        record.ScheduleTimeZoneId = zone;

        await AssertEngineRefusesAsync(WsA, record);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_idempotency_key_may_not_be_blank(string key)
    {
        // Blank would satisfy the unique index once and then refuse the next confirmation that also forgot.
        var record = NewRecord(null, await SeedProfileAsync(WsA));
        record.IdempotencyKey = key;

        await AssertEngineRefusesAsync(WsA, record);
    }

    [Fact]
    public async Task A_picture_travels_with_its_version_or_not_at_all()
    {
        var profileId = await SeedProfileAsync(WsA);
        var (assetId, _) = await SeedAssetVersionAsync(WsA);

        // An asset with no version is a picture nobody can reproduce; a version with no asset is a pin on
        // nothing, because a composite key with a null in it is not checked at all. The first half of this
        // test is also why the constraint names the nulls explicitly: a check passes unless it evaluates to
        // false, and the obvious `MediaAssetVersionNumber >= 1` is UNKNOWN for a null, which passes.
        var assetOnly = NewRecord(null, profileId);
        assetOnly.MediaAssetId = assetId;
        await AssertEngineRefusesAsync(WsA, assetOnly);

        var versionOnly = NewRecord(null, profileId);
        versionOnly.MediaAssetVersionNumber = 1;
        await AssertEngineRefusesAsync(WsA, versionOnly);
    }

    [Fact]
    public async Task Not_knowing_has_a_what_and_a_since()
    {
        var profileId = await SeedProfileAsync(WsA);

        var withoutSince = NewRecord(null, profileId);
        withoutSince.UncertainOperation = PublishingOperation.Submit;
        await AssertEngineRefusesAsync(WsA, withoutSince);

        var withoutWhat = NewRecord(null, profileId);
        withoutWhat.UncertainSince = Now;
        await AssertEngineRefusesAsync(WsA, withoutWhat);
    }

    [Fact]
    public async Task A_read_cannot_be_the_thing_in_doubt()
    {
        // Reconciliation is read-only, so repeating it is always safe: an unanswered one settles nothing and
        // blocks nothing. Only a write can leave an outcome genuinely in doubt.
        var record = NewRecord(null, await SeedProfileAsync(WsA));
        record.UncertainOperation = PublishingOperation.Reconcile;
        record.UncertainSince = Now;

        await AssertEngineRefusesAsync(WsA, record);
    }

    [Fact]
    public async Task Needing_attention_says_what_about()
    {
        var profileId = await SeedProfileAsync(WsA);

        var silent = NewRecord(null, profileId);
        silent.Status = PublishingRecordStatus.NeedsAttention;
        await AssertEngineRefusesAsync(WsA, silent);

        // Either reason will do: an outcome nobody knows, or a failure that needs a person.
        var uncertain = NewRecord(null, profileId);
        uncertain.Status = PublishingRecordStatus.NeedsAttention;
        uncertain.UncertainOperation = PublishingOperation.Submit;
        uncertain.UncertainSince = Now;

        var failed = NewRecord(null, profileId);
        failed.Status = PublishingRecordStatus.NeedsAttention;
        failed.FailureCategory = "provider_refused";

        await InAsync(WsA, async db =>
        {
            db.PublishingRecords.AddRange(uncertain, failed);
            await db.SaveChangesAsync(Ct);
        });
    }

    [Fact]
    public async Task A_lease_names_a_worker_and_an_expiry()
    {
        var profileId = await SeedProfileAsync(WsA);

        // A lease with no expiry could never be recovered from a worker that died holding it.
        var noExpiry = NewRecord(null, profileId);
        noExpiry.LeasedBy = Guid.NewGuid();
        await AssertEngineRefusesAsync(WsA, noExpiry);

        var noWorker = NewRecord(null, profileId);
        noWorker.LeaseExpiresAt = Now.AddMinutes(5);
        await AssertEngineRefusesAsync(WsA, noWorker);
    }

    [Fact]
    public async Task Attempt_counts_cannot_go_backwards()
    {
        var record = NewRecord(null, await SeedProfileAsync(WsA));
        record.SubmitAttempts = -1;

        await AssertEngineRefusesAsync(WsA, record);
    }

    // ---- Idempotency ----

    [Fact]
    public async Task The_same_key_twice_in_one_workspace_is_refused()
    {
        var profileId = await SeedProfileAsync(WsA);
        var key = $"confirm-{Guid.NewGuid():N}";

        var first = NewRecord(null, profileId);
        first.IdempotencyKey = key;

        await InAsync(WsA, async db =>
        {
            db.PublishingRecords.Add(first);
            await db.SaveChangesAsync(Ct);
        });

        // What stops a replayed confirmation buying a second post, durably — the shared IdempotencyRecord
        // expires after a day and this index does not.
        var replay = NewRecord(null, profileId);
        replay.IdempotencyKey = key;

        await AssertEngineRefusesAsync(WsA, replay);
    }

    [Fact]
    public async Task Two_workspaces_may_use_the_same_key_and_see_only_their_own_records()
    {
        var key = $"confirm-{Guid.NewGuid():N}";

        var mine = NewRecord(null, await SeedProfileAsync(WsA));
        mine.IdempotencyKey = key;
        var theirs = NewRecord(null, await SeedProfileAsync(WsB));
        theirs.IdempotencyKey = key;

        await InAsync(WsA, async db =>
        {
            db.PublishingRecords.Add(mine);
            await db.SaveChangesAsync(Ct);
        });

        await InAsync(WsB, async db =>
        {
            db.PublishingRecords.Add(theirs);
            await db.SaveChangesAsync(Ct);
        });

        var visible = await InAsync(
            WsA, db => db.PublishingRecords.AsNoTracking().Select(item => item.Id).ToListAsync(Ct));

        Assert.Contains(mine.Id, visible);
        Assert.DoesNotContain(theirs.Id, visible);

        // Not forbidden — invisible. The query filter is what makes the two answers one (tenancy.md).
        Assert.Null(await InAsync(
            WsA, db => db.PublishingRecords.AsNoTracking().FirstOrDefaultAsync(item => item.Id == theirs.Id, Ct)));
    }

    // ---- Two workspaces ----

    [Fact]
    public async Task A_record_cannot_name_another_workspaces_card()
    {
        var theirCard = await SeedCardAsync(WsA);
        var profileId = await SeedProfileAsync(WsB);

        await AssertEngineRefusesAsync(
            WsB, NewRecord(theirCard.Id, profileId), "FK_PublishingRecords_ContentCards");

        // The positive control: the same row shape with this workspace's own card goes in. Without it the
        // refusal above would also be satisfied by a key that refuses everything.
        var ownCard = await SeedCardAsync(WsB);

        await InAsync(WsB, async db =>
        {
            db.PublishingRecords.Add(NewRecord(ownCard.Id, profileId));
            await db.SaveChangesAsync(Ct);
        });
    }

    [Fact]
    public async Task A_record_cannot_name_another_workspaces_profile()
    {
        var theirProfileId = await SeedProfileAsync(WsA);

        await AssertEngineRefusesAsync(
            WsB, NewRecord(null, theirProfileId), "FK_PublishingRecords_PublishingProfiles");

        await InAsync(WsB, async db =>
        {
            db.PublishingRecords.Add(NewRecord(null, await SeedProfileAsync(WsB)));
            await db.SaveChangesAsync(Ct);
        });
    }

    [Fact]
    public async Task A_record_cannot_name_another_workspaces_picture()
    {
        var (theirAssetId, theirVersion) = await SeedAssetVersionAsync(WsA);
        var profileId = await SeedProfileAsync(WsB);

        var reaching = NewRecord(null, profileId);
        reaching.MediaAssetId = theirAssetId;
        reaching.MediaAssetVersionNumber = theirVersion;

        await AssertEngineRefusesAsync(WsB, reaching, "FK_PublishingRecords_MediaAssetVersions");

        var (ownAssetId, ownVersion) = await SeedAssetVersionAsync(WsB);
        var allowed = NewRecord(null, profileId);
        allowed.MediaAssetId = ownAssetId;
        allowed.MediaAssetVersionNumber = ownVersion;

        await InAsync(WsB, async db =>
        {
            db.PublishingRecords.Add(allowed);
            await db.SaveChangesAsync(Ct);
        });
    }

    [Fact]
    public async Task An_external_reference_cannot_be_attached_to_another_workspaces_record()
    {
        var record = await SeedRecordAsync(WsA);

        await AssertEngineRefusesAsync(
            WsB, NewReference(record.Id), "FK_ExternalPublicationReferences_PublishingRecords");
    }

    [Fact]
    public async Task An_attempt_cannot_be_written_against_another_workspaces_record()
    {
        var record = await SeedRecordAsync(WsA);

        await AssertEngineRefusesAsync(
            WsB,
            NewAttempt(record.Id, 1, PublishingOperation.Submit, PublishingAttemptOutcome.Accepted),
            "FK_PublishingDeliveryAttempts_PublishingRecords");
    }

    [Fact]
    public async Task The_trail_and_the_provider_mapping_are_invisible_to_the_other_workspace()
    {
        var record = await SeedRecordAsync(WsA);
        var reference = NewReference(record.Id);
        var attempt = NewAttempt(record.Id, 1, PublishingOperation.Submit, PublishingAttemptOutcome.Accepted);

        await InAsync(WsA, async db =>
        {
            db.ExternalPublicationReferences.Add(reference);
            db.PublishingDeliveryAttempts.Add(attempt);
            await db.SaveChangesAsync(Ct);
        });

        // The children carry the query filter as the root does. Asserted rather than assumed: the convention
        // discovers them by scanning the model, so a child that stopped implementing the contract would read
        // across workspaces while every constraint test above still passed.
        //
        // Scoped to the two rows this test made, not asserted over the whole set: the fixture is shared, so
        // workspace B legitimately holds rows of its own from other tests in this class.
        await InAsync(WsB, async db =>
        {
            Assert.Null(await db.ExternalPublicationReferences.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == reference.Id, Ct));
            Assert.Null(await db.PublishingDeliveryAttempts.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == attempt.Id, Ct));

            // And not reachable by the record either, which is how a panel would ask for them.
            Assert.Empty(await db.ExternalPublicationReferences.AsNoTracking()
                .Where(item => item.PublishingRecordId == record.Id).ToListAsync(Ct));
            Assert.Empty(await db.PublishingDeliveryAttempts.AsNoTracking()
                .Where(item => item.PublishingRecordId == record.Id).ToListAsync(Ct));
        });
    }

    [Fact]
    public async Task Another_workspaces_record_cannot_be_changed_or_removed_from_here()
    {
        var record = await SeedRecordAsync(WsA);

        // There is nothing to mutate, because there is nothing to load: the filter is the whole mechanism.
        await InAsync(WsB, async db =>
        {
            Assert.Null(await db.PublishingRecords.FirstOrDefaultAsync(item => item.Id == record.Id, Ct));
            Assert.Equal(0, await db.PublishingRecords.CountAsync(item => item.Id == record.Id, Ct));
        });

        // And ownership cannot be handed over by an update, which is the other way a record could move
        // workspaces (tenancy.md). WorkspaceOwnershipInterceptor refuses before anything reaches the store.
        await InAsync(WsA, async db =>
        {
            var tracked = await db.PublishingRecords.SingleAsync(item => item.Id == record.Id, Ct);
            tracked.WorkspaceId = WsB;

            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
        });
    }

    // ---- The provider's own id, and only here ----

    [Fact]
    public async Task One_provider_post_maps_to_one_record()
    {
        var first = await SeedRecordAsync(WsA);
        var second = await SeedRecordAsync(WsA);

        var externalId = $"update-{Guid.NewGuid():N}";
        var reference = NewReference(first.Id);
        reference.ExternalUpdateId = externalId;

        await InAsync(WsA, async db =>
        {
            db.ExternalPublicationReferences.Add(reference);
            await db.SaveChangesAsync(Ct);
        });

        // What lets a reconciliation that finds a post resolve it to the record that made it, instead of
        // concluding there is a second post to make.
        var duplicate = NewReference(second.Id);
        duplicate.ExternalUpdateId = externalId;

        await AssertEngineRefusesAsync(WsA, duplicate);
    }

    [Fact]
    public async Task A_record_has_at_most_one_provider_post()
    {
        var record = await SeedRecordAsync(WsA);

        await InAsync(WsA, async db =>
        {
            db.ExternalPublicationReferences.Add(NewReference(record.Id));
            await db.SaveChangesAsync(Ct);
        });

        await AssertEngineRefusesAsync(WsA, NewReference(record.Id));
    }

    [Fact]
    public async Task Two_workspaces_may_hold_the_same_provider_post_id()
    {
        // Uniqueness is workspace-relative here as everywhere: a global unique index would refuse a row for a
        // reason that discloses another workspace's existence, which is the leak 404s elsewhere avoid.
        var externalId = $"update-{Guid.NewGuid():N}";

        foreach (var workspaceId in new[] { WsA, WsB })
        {
            var record = await SeedRecordAsync(workspaceId);
            var reference = NewReference(record.Id);
            reference.ExternalUpdateId = externalId;

            await InAsync(workspaceId, async db =>
            {
                db.ExternalPublicationReferences.Add(reference);
                await db.SaveChangesAsync(Ct);
            });
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_external_reference_without_an_id_is_not_a_mapping(string externalId)
    {
        var record = await SeedRecordAsync(WsA);
        var reference = NewReference(record.Id);
        reference.ExternalUpdateId = externalId;

        await AssertEngineRefusesAsync(WsA, reference);
    }

    // ---- The trail ----

    [Fact]
    public async Task An_attempt_records_an_outcome_and_nothing_a_provider_said()
    {
        var record = await SeedRecordAsync(WsA);
        var attempt = NewAttempt(record.Id, 1, PublishingOperation.Submit, PublishingAttemptOutcome.Unknown);
        attempt.CompletedAt = null;
        attempt.FailureCategory = "provider_timeout";
        attempt.FailureSummary = "The provider did not answer within the configured timeout.";

        await InAsync(WsA, async db =>
        {
            db.PublishingDeliveryAttempts.Add(attempt);
            await db.SaveChangesAsync(Ct);
        });

        var loaded = await InAsync(
            WsA, db => db.PublishingDeliveryAttempts.AsNoTracking().SingleAsync(item => item.Id == attempt.Id, Ct));

        Assert.Equal(WsA, loaded.WorkspaceId);
        Assert.Equal(PublishingAttemptOutcome.Unknown, loaded.Outcome);
        Assert.Null(loaded.CompletedAt);
        Assert.Null(loaded.ProviderStatusCode);
    }

    [Fact]
    public async Task An_attempt_is_write_once()
    {
        var record = await SeedRecordAsync(WsA);
        var attempt = NewAttempt(record.Id, 1, PublishingOperation.Submit, PublishingAttemptOutcome.Refused);

        await InAsync(WsA, async db =>
        {
            db.PublishingDeliveryAttempts.Add(attempt);
            await db.SaveChangesAsync(Ct);
        });

        // ImmutableRecordInterceptor refuses both: a trail that can be edited is not a trail.
        await InAsync(WsA, async db =>
        {
            var tracked = await db.PublishingDeliveryAttempts.SingleAsync(item => item.Id == attempt.Id, Ct);
            tracked.Outcome = PublishingAttemptOutcome.Accepted;

            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
        });

        await InAsync(WsA, async db =>
        {
            db.PublishingDeliveryAttempts.Remove(
                await db.PublishingDeliveryAttempts.SingleAsync(item => item.Id == attempt.Id, Ct));

            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
        });
    }

    [Fact]
    public async Task Two_attempts_on_one_record_cannot_be_the_same_number()
    {
        var record = await SeedRecordAsync(WsA);

        await InAsync(WsA, async db =>
        {
            db.PublishingDeliveryAttempts.Add(
                NewAttempt(record.Id, 1, PublishingOperation.Submit, PublishingAttemptOutcome.Unavailable));
            await db.SaveChangesAsync(Ct);
        });

        // Two workers writing at once cannot both be the first attempt.
        await AssertEngineRefusesAsync(
            WsA, NewAttempt(record.Id, 1, PublishingOperation.Submit, PublishingAttemptOutcome.Accepted));
    }

    [Fact]
    public async Task An_attempt_is_numbered_from_one()
    {
        var record = await SeedRecordAsync(WsA);

        await AssertEngineRefusesAsync(
            WsA, NewAttempt(record.Id, 0, PublishingOperation.Submit, PublishingAttemptOutcome.Accepted));
    }

    [Fact]
    public async Task An_attempt_must_say_what_it_asked_for_and_how_it_ended()
    {
        var record = await SeedRecordAsync(WsA);

        var noOperation = NewAttempt(record.Id, 1, default, PublishingAttemptOutcome.Accepted);
        await AssertEngineRefusesAsync(WsA, noOperation);

        var noOutcome = NewAttempt(record.Id, 2, PublishingOperation.Submit, default);
        await AssertEngineRefusesAsync(WsA, noOutcome);
    }

    [Fact]
    public async Task A_provider_status_code_is_an_http_status_or_nothing()
    {
        var record = await SeedRecordAsync(WsA);

        var zero = NewAttempt(record.Id, 1, PublishingOperation.Submit, PublishingAttemptOutcome.Unknown);
        zero.ProviderStatusCode = 0;

        await AssertEngineRefusesAsync(WsA, zero);
    }

    // ---- Retention ----

    [Fact]
    public async Task A_record_outlives_the_card_it_was_confirmed_from_once_it_is_detached()
    {
        var card = await SeedCardAsync(WsA);
        var record = await SeedRecordAsync(WsA, card.Id);

        // While it points at the plan, the plan cannot be taken out from under it. 13.6c's policy — a live
        // publication blocks the delete, a terminal one does not — is enforced above this, in the card's own
        // seam; the restricted key is what makes a mistake there a refusal rather than lost history.
        await InAsync(WsA, async db =>
        {
            db.ContentCards.Remove(await db.ContentCards.SingleAsync(item => item.Id == card.Id, Ct));

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        });

        await InAsync(WsA, async db =>
        {
            (await db.PublishingRecords.SingleAsync(item => item.Id == record.Id, Ct)).ContentCardId = null;
            await db.SaveChangesAsync(Ct);
        });

        await InAsync(WsA, async db =>
        {
            db.ContentCards.Remove(await db.ContentCards.SingleAsync(item => item.Id == card.Id, Ct));
            await db.SaveChangesAsync(Ct);
        });

        // The delivery record survives the plan: it is the only thing that says content went to a provider.
        var survivor = await InAsync(
            WsA, db => db.PublishingRecords.AsNoTracking().SingleAsync(item => item.Id == record.Id, Ct));

        Assert.Null(survivor.ContentCardId);
        Assert.Equal("Soda bread, out of the oven at last.", survivor.ContentText);
    }

    [Fact]
    public async Task Deleting_a_workspace_erases_its_ledger()
    {
        // Three tables reach the workspace through the record, and the record holds restricted keys to a card,
        // a profile and an asset version that all cascade from the same workspace. If SQL Server checked those
        // NO ACTION constraints mid-cascade rather than at the end of the statement, a workspace with one
        // scheduled post could never be erased.
        var workspaceId = Guid.NewGuid();

        await InAsync(WsA, async db =>
        {
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId,
                Name = "Doomed",
                Slug = $"doomed-{workspaceId:N}",
                CreatedAt = Now,
            });
            await db.SaveChangesAsync(Ct);
        });

        var card = await SeedCardAsync(workspaceId);
        var (assetId, versionNumber) = await SeedAssetVersionAsync(workspaceId);
        var record = NewRecord(card.Id, await SeedProfileAsync(workspaceId));
        record.MediaAssetId = assetId;
        record.MediaAssetVersionNumber = versionNumber;

        await InAsync(workspaceId, async db =>
        {
            db.PublishingRecords.Add(record);
            await db.SaveChangesAsync(Ct);

            db.ExternalPublicationReferences.Add(NewReference(record.Id));
            db.PublishingDeliveryAttempts.Add(
                NewAttempt(record.Id, 1, PublishingOperation.Submit, PublishingAttemptOutcome.Accepted));
            await db.SaveChangesAsync(Ct);
        });

        // An ordinary scope: Workspaces is not workspace-owned, so no filter stands between this and the row.
        await using var erase = fixture.ScopeFor(WsA);
        var db = SqlServerRecipeFixture.Db(erase);
        db.Workspaces.Remove(await db.Workspaces.SingleAsync(item => item.Id == workspaceId, Ct));
        await db.SaveChangesAsync(Ct);

        // Counted with raw SQL because the erased workspace can no longer be resolved to a scope that would
        // see its rows. The attempts are immutable, so this database-level cascade is the one path that may
        // remove them.
        foreach (var table in new[]
                 {
                     "PublishingRecords", "ExternalPublicationReferences", "PublishingDeliveryAttempts",
                 })
        {
            var remaining = await db.Database
                .SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM {table} WHERE WorkspaceId = {{0}}", workspaceId)
                .SingleAsync(Ct);

            Assert.Equal(0, remaining);
        }
    }

    [Fact]
    public void The_content_ceiling_is_the_one_the_social_seam_writes_to()
    {
        // Stated in two places because the Content module's policy may not cross a module boundary. What a
        // creator can write has to be publishable, so the two numbers have to agree — here, not in review.
        Assert.Equal(ContentPolicy.SocialBodyMaxLength, PublishingLedgerPolicy.ContentTextMaxLength);
    }

    [Fact]
    public async Task A_records_key_is_as_long_as_the_one_a_command_accepts()
    {
        // The key on the row is the key the caller sent on the command (api-contract.md). Two ceilings would
        // make a key the command accepted unstorable, which is a 500 on a legitimate retry.
        var maxLength = await InAsync(WsA, db => Task.FromResult(
            db.Model.FindEntityType(typeof(PublishingRecord))!
                .FindProperty(nameof(PublishingRecord.IdempotencyKey))!
                .GetMaxLength()));

        Assert.Equal(IdempotencyPolicy.KeyMaxLength, maxLength);
    }

    // ---- Helpers ----

    private static PublishingRecord NewRecord(Guid? cardId, Guid profileId) => new()
    {
        Id = Guid.NewGuid(),
        ContentCardId = cardId,
        PublishingProfileId = profileId,
        Platform = PublishingPlatform.Instagram,
        ContentText = "Soda bread, out of the oven at last.",
        ScheduledAt = Now.AddDays(3),
        ScheduleTimeZoneId = "America/Chicago",
        ScheduledLocal = new DateTime(2026, 9, 26, 7, 0, 0, DateTimeKind.Unspecified),
        Status = PublishingRecordStatus.Pending,
        StatusChangedAt = Now,
        AvailableAt = Now,
        ConfirmedByMembershipId = SqlServerRecipeFixture.AuthorOne,
        CreatedAt = Now,
        IdempotencyKey = $"confirm-{Guid.NewGuid():N}",
    };

    private static ExternalPublicationReference NewReference(Guid recordId) => new()
    {
        Id = Guid.NewGuid(),
        PublishingRecordId = recordId,
        ProviderKey = PublishingPolicy.BufferProviderKey,
        ExternalUpdateId = $"update-{Guid.NewGuid():N}",
        FirstSeenAt = Now,
    };

    private static PublishingDeliveryAttempt NewAttempt(
        Guid recordId, int number, PublishingOperation operation, PublishingAttemptOutcome outcome) => new()
    {
        Id = Guid.NewGuid(),
        PublishingRecordId = recordId,
        AttemptNumber = number,
        Operation = operation,
        Outcome = outcome,
        StartedAt = Now,
        CompletedAt = Now.AddSeconds(2),
        CorrelationId = Guid.NewGuid(),
    };

    private async Task<PublishingRecord> SeedRecordAsync(Guid workspaceId, Guid? cardId = null)
    {
        var record = NewRecord(cardId, await SeedProfileAsync(workspaceId));

        await InAsync(workspaceId, async db =>
        {
            db.PublishingRecords.Add(record);
            await db.SaveChangesAsync(Ct);
        });

        return record;
    }

    private async Task<Guid> SeedProfileAsync(Guid workspaceId)
    {
        var profile = new PublishingProfile
        {
            Id = Guid.NewGuid(),
            ProviderKey = PublishingPolicy.BufferProviderKey,
            Platform = PublishingPlatform.Instagram,
            DisplayName = "The test kitchen",
            TimeZoneId = "America/Chicago",
            Status = PublishingProfileStatus.Ready,
            FirstSeenAt = Now,
            LastSeenAt = Now,
        };

        await InAsync(workspaceId, async db =>
        {
            db.PublishingProfiles.Add(profile);
            await db.SaveChangesAsync(Ct);
        });

        return profile.Id;
    }

    private async Task<ContentCard> SeedCardAsync(Guid workspaceId)
    {
        var card = new ContentCard
        {
            Id = Guid.NewGuid(),
            Title = "Soda bread, Friday",
            Column = ContentCardColumn.InProcess,
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

    private async Task<(Guid AssetId, int VersionNumber)> SeedAssetVersionAsync(Guid workspaceId)
    {
        var asset = SeededMediaAsset.For(workspaceId, at: Now);
        var version = SeededMediaAsset.VersionOf(asset);

        await InAsync(workspaceId, async db =>
        {
            db.MediaAssets.Add(asset);
            db.MediaAssetVersions.Add(version);
            await db.SaveChangesAsync(Ct);
        });

        return (asset.Id, version.VersionNumber);
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

    /// <param name="constraint">
    /// The constraint or index expected to do the refusing. Worth naming wherever another constraint could
    /// plausibly fire first: a test that accepts any <see cref="DbUpdateException"/> passes just as happily
    /// when the row was refused for the wrong reason, which is how a key that stopped being checked goes
    /// unnoticed.
    /// </param>
    private Task AssertEngineRefusesAsync(Guid workspaceId, object row, string? constraint = null) =>
        InAsync(workspaceId, async db =>
        {
            db.Add(row);
            var refusal = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));

            if (constraint is not null)
            {
                Assert.Contains(constraint, refusal.InnerException?.Message ?? refusal.Message, StringComparison.Ordinal);
            }
        });
}
