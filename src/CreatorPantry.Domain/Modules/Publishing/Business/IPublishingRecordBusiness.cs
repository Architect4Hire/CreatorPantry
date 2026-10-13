using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Publishing.Data;
using CreatorPantry.Domain.Modules.Publishing.Data.Entities;
using CreatorPantry.Domain.Modules.Publishing.Gateways;
using CreatorPantry.Domain.Modules.Publishing.Managers;

namespace CreatorPantry.Domain.Modules.Publishing.Business;

/// <summary>The rules for confirming and submitting one publication (PUB-002).</summary>
public interface IPublishingRecordBusiness
{
    /// <summary>
    /// Confirms a publication, commits it, and asks the provider to take it.
    /// </summary>
    /// <remarks>
    /// Returns a record whatever the provider said. A provider that refused, throttled or did not answer is
    /// the record's own state, not a failed command: the confirmation happened, and the ledger is the honest
    /// account of what became of it. Only a refusal of the <em>confirmation</em> — something the caller can
    /// fix — is an <see cref="OperationError"/>.
    /// </remarks>
    Task<OperationResult<PublishingScheduleOutcome>> ScheduleAsync(
        string actorUserId,
        SchedulePublishingViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>One record of the resolved workspace. An unknown id and another workspace's are one answer.</summary>
    Task<OperationResult<PublishingRecordServiceModel>> GetAsync(Guid recordId, CancellationToken cancellationToken);

    /// <summary>
    /// One card's publications (13.10d).
    /// </summary>
    /// <remarks>
    /// An empty list for a card that has none, for a card this workspace does not have, and for another
    /// workspace's — the three are one answer, so asking about an id cannot reveal that a neighbour owns it.
    /// </remarks>
    Task<PublishingRecordListServiceModel> ListForCardAsync(Guid contentCardId, CancellationToken cancellationToken);

    /// <summary>
    /// Stops one publication going out, asking the provider to remove its post when there is one to remove
    /// (PUB-003).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Repeating it is safe.</strong> A record already cancelled answers with itself; a removal whose
    /// outcome nobody knows is asked for again, because asking twice cannot remove two posts.
    /// </para>
    /// <para>
    /// <strong>An unknown submit is never cancelled.</strong> Without the provider's id for a post there is
    /// nothing to ask it to remove, so the refusal says exactly that rather than recording a cancellation
    /// that did not happen.
    /// </para>
    /// </remarks>
    Task<OperationResult<PublishingRecordServiceModel>> CancelAsync(
        string actorUserId, Guid recordId, CancelPublishingViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Submits a record the worker has claimed, through the same seam and the same transition gate a request
    /// uses (13.10c).
    /// </summary>
    /// <remarks>
    /// Deliberately not a second submit path. The gate inside <c>MarkSubmittingAsync</c> is what makes two
    /// claimants impossible, and reusing it is what stops the worker becoming a way around it. Returns the
    /// status the record ended in; the claim's lease is released by the write.
    /// </remarks>
    Task<PublishingRecordStatus> SubmitPendingAsync(
        PublishingReconcileTarget target, string providerKey, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the provider again to remove a post a claimed record is still trying to stop (13.10c).
    /// </summary>
    /// <remarks>
    /// Safe precisely because a removal repeated cannot remove two posts. The same path a creator's repeated
    /// cancellation takes.
    /// </remarks>
    Task<PublishingRecordStatus> CancelClaimedAsync(
        PublishingReconcileTarget target, string externalUpdateId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPublishingRecordBusiness"/>
/// <remarks>
/// <para>
/// <strong>The order of the steps is the design.</strong> Validate, commit a pending row, mark it as being
/// submitted, call the provider with no transaction open, commit what it said. Each boundary is a state the
/// ledger can be found in after a crash, and none of them is a state that has to be guessed at
/// (publishing.md).
/// </para>
/// <para>
/// <strong>Nothing is ever submitted twice on purpose or by accident.</strong> The provider offers no
/// idempotency key, so the ledger's own unique key is the guard: a replay reads the record the first
/// confirmation made and returns it, and a record that is not <see cref="PublishingRecordStatus.Pending"/> is
/// never submitted at all.
/// </para>
/// <para>
/// <strong>A picture cannot be published yet, and that is said rather than worked around.</strong> The
/// provider fetches images by public URL and this server's object store issues no addresses (media.md), so a
/// confirmation naming one is refused. Sending the text alone would leave a creator believing a picture went
/// out — and a ledger row that said so.
/// </para>
/// </remarks>
internal sealed class PublishingRecordBusiness(
    IPublishingRecordDataLayer dataLayer,
    IContentCardLookupFacade cards,
    IMediaAssetLookupFacade media,
    IBrandProfileFacade brand,
    ITimeZoneConverter zones,
    IWorkspaceContext workspace,
    IClock clock) : IPublishingRecordBusiness
{
    private const string CannotSchedule = "That publication could not be scheduled as described.";

    public async Task<OperationResult<PublishingScheduleOutcome>> ScheduleAsync(
        string actorUserId,
        SchedulePublishingViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var provider = dataLayer.DescribeProvider();

        // A provider credential is one account at the provider, configured together with the single workspace
        // that account belongs to. Every other workspace is answered exactly as a deployment with no provider
        // at all, so this cannot be used to learn that a neighbour has one (tenancy.md).
        if (provider is not { Configured: true, ProviderKey: not null, WorkspaceId: { } owner }
            || owner == Guid.Empty
            || owner != workspace.WorkspaceId)
        {
            return Refused(new OperationError(
                PublishingErrorCodes.ProviderNotConfigured,
                "This workspace has no publishing provider, so nothing can be scheduled yet.",
                new Dictionary<string, string[]>()));
        }

        var key = idempotencyKey.Trim();

        // Asked before anything is validated or sent: a replay must cost the provider nothing, and must not
        // be able to fail validation that the first attempt passed.
        if (await ReplayAsync(key, model, cancellationToken) is { } replay)
        {
            return replay;
        }

        var scheduledAt = model.ScheduledAt!.Value;
        var now = clock.UtcNow;

        if (scheduledAt < now + PublishingLedgerPolicy.MinimumScheduleLead)
        {
            return Invalid((
                nameof(model.ScheduledAt),
                $"Schedule it at least {PublishingLedgerPolicy.MinimumScheduleLead.TotalMinutes:N0} minutes from now."));
        }

        if (scheduledAt > now + PublishingLedgerPolicy.MaximumScheduleHorizon)
        {
            return Invalid((
                nameof(model.ScheduledAt),
                $"That is further ahead than {PublishingLedgerPolicy.MaximumScheduleHorizon.TotalDays:N0} days."));
        }

        if (!await cards.ExistsAsync(model.ContentCardId, cancellationToken))
        {
            return Invalid((nameof(model.ContentCardId), "That card is not on this workspace's board."));
        }

        var target = await dataLayer.FindTargetAsync(model.PublishingProfileId, provider.ProviderKey, cancellationToken);
        if (target is null)
        {
            return Invalid((nameof(model.PublishingProfileId), "That profile is not one of this workspace's."));
        }

        if (target.Status is not PublishingProfileStatus.Ready)
        {
            return Refused(new OperationError(
                PublishingErrorCodes.ProfileNotReady,
                target.Status switch
                {
                    PublishingProfileStatus.NeedsReconnecting =>
                        "That profile needs reconnecting at the publishing provider before it can be posted to.",
                    PublishingProfileStatus.Locked =>
                        "The publishing provider will not post to that profile at the moment.",
                    _ => "The publishing provider no longer lists that profile.",
                },
                new Dictionary<string, string[]>()));
        }

        // The caller's copy of the profile list is out of date. Refused rather than published to a network
        // they did not mean.
        if (model.ExpectedPlatform is { } expected && expected != target.Platform)
        {
            return Refused(new OperationError(
                PublishingErrorCodes.PlatformMismatch,
                "That profile posts to a different network than the request expected. Read the profiles again.",
                new Dictionary<string, string[]>()));
        }

        if (model.MediaAssetId is { } assetId)
        {
            // Ownership first, so a picture that is not this workspace's is told apart from one that cannot be
            // published yet. Both refusals are about the same field; only one of them is the creator's mistake.
            var linkable = await media.ResolveLinkTargetAsync(assetId, model.MediaAssetVersionNumber, cancellationToken);
            if (linkable is not MediaAssetLinkTarget.Linkable)
            {
                return Invalid((
                    linkable is MediaAssetLinkTarget.VersionNotFound
                        ? nameof(model.MediaAssetVersionNumber)
                        : nameof(model.MediaAssetId),
                    linkable is MediaAssetLinkTarget.VersionNotFound
                        ? "That picture does not have that version."
                        : "That picture is not in this workspace."));
            }

            return Refused(new OperationError(
                PublishingErrorCodes.MediaNotPublishable,
                "A picture cannot be published through the provider yet. Schedule the text, and add the picture "
                    + "at the provider for now.",
                new Dictionary<string, string[]>()));
        }

        // The workspace's own scheduling zone, never a guess and never anything from the request.
        var profile = await brand.GetAsync(cancellationToken);
        if (!profile.Succeeded || profile.Value?.TimeZoneId is not { Length: > 0 } zoneId || !zones.IsValidZone(zoneId))
        {
            return Refused(new OperationError(
                PublishingErrorCodes.TimeZoneUnset,
                "Set the workspace's time zone in its brand profile before scheduling a publication.",
                new Dictionary<string, string[]>()));
        }

        // A fresh key for a publication that is already going out. The key's unique index cannot see this —
        // two keys are two rows — so it is asked for directly, and only live states count: a failed or
        // cancelled publication is one a creator may legitimately confirm again (publishing.md).
        if (await dataLayer.FindLiveDuplicateAsync(
                model.ContentCardId, target.ProfileId, model.Text!.Trim(), scheduledAt, cancellationToken) is { } live)
        {
            // It may be this very request, arriving twice: the first copy committed between the key check
            // above and here. A retry is a replay, not a duplicate.
            if (await ReplayAsync(key, model, cancellationToken) is { } raced)
            {
                return raced;
            }

            return Refused(new OperationError(
                PublishingErrorCodes.DuplicatePublication,
                "That publication is already going out to that profile at that time.",
                new Dictionary<string, string[]>(),
                new Dictionary<string, object?> { ["publishingRecordId"] = live }));
        }

        var record = new PublishingRecord
        {
            Id = Guid.NewGuid(),
            ContentCardId = model.ContentCardId,
            PublishingProfileId = target.ProfileId,

            // The profile's network, not the request's: the request's is only ever a guard.
            Platform = target.Platform,
            ContentText = model.Text!.Trim(),
            ScheduledAt = scheduledAt,
            ScheduleTimeZoneId = zoneId,
            ScheduledLocal = zones.ToZoned(scheduledAt, zoneId).DateTime,
            Status = PublishingRecordStatus.Pending,
            StatusChangedAt = now,
            AvailableAt = now,
            ConfirmedByMembershipId = workspace.MembershipId,
            CreatedAt = now,
            IdempotencyKey = key,
        };

        var confirmation = await dataLayer.ConfirmAsync(
            record,
            new AuditEntry(
                actorUserId,
                PublishingAuditActions.Confirmed,
                PublishingAuditActions.ResourceType,
                record.Id.ToString("D"),
                CorrelationId(),

                // Ids, a network and a time. Never the text that is being published.
                $"Confirmed a publication to profile {target.ProfileId:D} on {target.Platform} "
                    + $"for {scheduledAt.UtcDateTime:O}, from card {model.ContentCardId:D}",
                AfterReference: $"status:{PublishingRecordStatus.Pending}"),
            cancellationToken);

        // Two requests raced the same key and this one lost. The winner's record is the answer, and the
        // provider has been asked exactly once.
        if (confirmation is PublishingConfirmOutcome.KeyTaken)
        {
            // The same comparison the sequential replay makes: losing a race is not a reason to hand back a
            // record for a different request.
            return await ReplayAsync(key, model, cancellationToken)
                ?? Refused(new OperationError(
                    PublishingErrorCodes.ScheduleConflict,
                    "Another confirmation with that key is in flight. Try again.",
                    new Dictionary<string, string[]>()));
        }

        // Two confirmations raced the same publication and the index refused this one. The creator is told
        // what is already going out, exactly as the check before the write would have told them.
        if (confirmation is PublishingConfirmOutcome.DuplicateLive)
        {
            if (await ReplayAsync(key, model, cancellationToken) is { } replayed)
            {
                return replayed;
            }

            var raced = await dataLayer.FindLiveDuplicateAsync(
                model.ContentCardId, target.ProfileId, model.Text!.Trim(), scheduledAt, cancellationToken);

            return Refused(new OperationError(
                PublishingErrorCodes.DuplicatePublication,
                "That publication is already going out to that profile at that time.",
                new Dictionary<string, string[]>(),
                raced is null ? null : new Dictionary<string, object?> { ["publishingRecordId"] = raced }));
        }

        if (confirmation is PublishingConfirmOutcome.Refused)
        {
            // Nothing was written and nothing was sent, and it is not the caller's doing: the same request
            // may well succeed.
            return Refused(new OperationError(
                PublishingErrorCodes.ScheduleUnavailable,
                "That publication could not be confirmed just now. Try again.",
                new Dictionary<string, string[]>()));
        }

        return await SubmitAsync(actorUserId, record.Id, target, provider.ProviderKey, false, cancellationToken);
    }

    public async Task<OperationResult<PublishingRecordServiceModel>> GetAsync(
        Guid recordId, CancellationToken cancellationToken) =>
        await dataLayer.FindAsync(recordId, cancellationToken) is { } row
            ? OperationResult<PublishingRecordServiceModel>.Success(ToModel(row))
            : NotFound();

    public async Task<OperationResult<PublishingRecordServiceModel>> CancelAsync(
        string actorUserId, Guid recordId, CancelPublishingViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (await dataLayer.FindAsync(recordId, cancellationToken) is not { } row)
        {
            return NotFound();
        }

        // Already stopped. Answered with itself before the token is looked at, because "repeating a
        // cancellation is safe" has to be true of the retry a creator actually makes: the one that quotes the
        // token it was given, which the first cancellation has already moved on from.
        if (row.Status is PublishingRecordStatus.Cancelled)
        {
            return OperationResult<PublishingRecordServiceModel>.Success(ToModel(row));
        }

        // Otherwise the state the caller was looking at, quoted back. The race this catches is a post going
        // out between the screen being drawn and Cancel being pressed.
        if (!PublishingRecordConcurrencyToken.Matches(model.ExpectedConcurrencyToken, row.RowVersion))
        {
            return Conflict(
                PublishingErrorCodes.RecordConflict,
                "That publication has changed since it was read. Read it again and look before cancelling.");
        }

        switch (row.Status)
        {
            case PublishingRecordStatus.Published:
                return Conflict(
                    PublishingErrorCodes.AlreadyPublished,
                    "That publication has already gone out, so it cannot be cancelled.");

            case PublishingRecordStatus.Failed:
                return Conflict(
                    PublishingErrorCodes.NotCancellable,
                    "That publication failed and will not go out, so there is nothing to cancel.");

            // A submit is in flight, or its outcome is unknown. Either way there is no post this can name.
            case PublishingRecordStatus.Submitting:
                return Conflict(
                    PublishingErrorCodes.OutcomeUnknown,
                    "Whether that publication was sent is not yet known, so it cannot be cancelled yet.");
        }

        var externalId = await dataLayer.FindExternalUpdateIdAsync(recordId, cancellationToken);

        if (externalId is null)
        {
            // Nothing the provider ever acknowledged. One exception: a submit whose outcome is unknown may
            // have made a post this server cannot name, and that must not be reported as cancelled.
            return row.UncertainOperation is PublishingOperation.Submit
                ? Conflict(
                    PublishingErrorCodes.OutcomeUnknown,
                    "Whether that publication was sent is not yet known, so it cannot be cancelled yet.")
                : await CancelLocallyAsync(actorUserId, row, cancellationToken);
        }

        var provider = dataLayer.DescribeProvider();
        if (provider is not { Configured: true, ProviderKey: not null, WorkspaceId: { } owner }
            || owner != workspace.WorkspaceId)
        {
            return Conflict(
                PublishingErrorCodes.ProviderNotConfigured,
                "This workspace has no publishing provider, so its post cannot be removed from here.");
        }

        return await AskProviderToRemoveAsync(actorUserId, row, externalId, false, cancellationToken);
    }

    /// <summary>A publication that never reached the provider. Stopped here, with nothing asked of anyone.</summary>
    private async Task<OperationResult<PublishingRecordServiceModel>> CancelLocallyAsync(
        string? actorUserId, PublishingRecordRow row, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var recorded = await dataLayer.RecordOutcomeAsync(
            new PublishingOutcomeWrite(
                row.Id,
                PublishingRecordStatus.Cancelled,
                now,
                SubmittedAt: null,
                CompletedAt: now,
                UncertainOperation: null,
                UncertainSince: null,
                AvailableAt: now,
                FailureCategory: null,
                FailureSummary: null,
                ProviderKey: string.Empty,
                ExternalUpdateId: null,

                // No attempt row: the provider was never asked, and a trail that showed a call here would be
                // describing something that did not happen.
                Attempt: null,
                new AuditEntry(
                    actorUserId,
                    PublishingAuditActions.Cancelled,
                    PublishingAuditActions.ResourceType,
                    row.Id.ToString("D"),
                    CorrelationId(),
                    $"Cancelled publication {row.Id:D} before it was sent to the provider",
                    BeforeReference: $"status:{row.Status}",
                    AfterReference: $"status:{PublishingRecordStatus.Cancelled}")),
            cancellationToken);

        return recorded is null
            ? Conflict(
                PublishingErrorCodes.RecordConflict,
                "That publication could not be cancelled just now. Read it again and try once more.")
            : OperationResult<PublishingRecordServiceModel>.Success(ToModel(recorded));
    }

    /// <summary>
    /// The removal: committed as begun, asked for with nothing open, then settled by what came back.
    /// </summary>
    private async Task<OperationResult<PublishingRecordServiceModel>> AskProviderToRemoveAsync(
        string? actorUserId,
        PublishingRecordRow row,
        string externalId,
        bool releaseLease,
        CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;

        var marked = await dataLayer.MarkCancellingAsync(
            row.Id,
            startedAt,
            startedAt + PublishingPolicy.UnavailableBackoff,
            new AuditEntry(
                actorUserId,
                PublishingAuditActions.CancellationRequested,
                PublishingAuditActions.ResourceType,
                row.Id.ToString("D"),
                CorrelationId(),
                $"Asked the provider to remove the post for publication {row.Id:D}",
                BeforeReference: $"status:{row.Status}",
                AfterReference: $"status:{PublishingRecordStatus.Cancelling}"),
            cancellationToken);

        // Something moved the record between the read above and this write, so the caller's token no longer
        // describes it. Refused rather than acted on: the state it is in now may be one that must not be
        // cancelled at all.
        if (marked.Row is null || !marked.Transitioned)
        {
            return Conflict(
                PublishingErrorCodes.RecordConflict,
                "That publication changed while it was being cancelled. Read it again and look before retrying.");
        }

        PublishingCancellationResult result;
        try
        {
            result = await dataLayer.CancelAsync(new PublishingCancellation(externalId), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The caller gave up. Whether the post is gone is unknown, and asking again is safe — so the
            // record says so, written with a token that cannot be cancelled.
            await dataLayer.RecordOutcomeAsync(
                NextAfterCancel(
                    marked.Row,
                    new PublishingCancellationResult(
                        PublishingCancellationOutcome.Unknown,
                        FailureCategory: "request_abandoned",
                        FailureSummary: "The request was abandoned while the provider was being asked."),
                    startedAt,
                    clock.UtcNow,
                    actorUserId) with { ReleaseLease = releaseLease },
                CancellationToken.None);

            throw;
        }

        var recorded = await dataLayer.RecordOutcomeAsync(
            NextAfterCancel(marked.Row, result, startedAt, clock.UtcNow, actorUserId) with
            {
                ReleaseLease = releaseLease,
            },
            CancellationToken.None);

        return OperationResult<PublishingRecordServiceModel>.Success(ToModel(recorded ?? marked.Row));
    }

    /// <summary>
    /// What one answer to a removal means for the record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Only two answers end in cancelled</strong>: the provider removed the post, or it does not have
    /// it. Everything else leaves the publication saying it is still going out, or still trying, or that
    /// nobody knows — never that it was stopped.
    /// </para>
    /// <para>
    /// <strong>A refusal puts it back to scheduled</strong>, because that is what it is: the post stands, and
    /// the category says why the removal did not happen.
    /// </para>
    /// </remarks>
    private PublishingOutcomeWrite NextAfterCancel(
        PublishingRecordRow record,
        PublishingCancellationResult result,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        string? actorUserId)
    {
        var gone = result.Outcome
            is PublishingCancellationOutcome.Cancelled or PublishingCancellationOutcome.AlreadyGone;

        var stillGoingOut = result.Outcome
            is PublishingCancellationOutcome.NotDeletable
            or PublishingCancellationOutcome.Refused
            or PublishingCancellationOutcome.NotConfigured;

        var uncertain = result.Outcome is PublishingCancellationOutcome.Unknown;

        var status = gone ? PublishingRecordStatus.Cancelled
            : stillGoingOut ? PublishingRecordStatus.Scheduled
            : uncertain && record.CancelAttempts >= PublishingLedgerPolicy.MaxCancelAttempts
                ? PublishingRecordStatus.NeedsAttention

                // Throttled, or an unknown outcome with budget left: still trying to stop it.
                : PublishingRecordStatus.Cancelling;

        var outcome = result.Outcome switch
        {
            PublishingCancellationOutcome.Cancelled or PublishingCancellationOutcome.AlreadyGone =>
                PublishingAttemptOutcome.Accepted,
            PublishingCancellationOutcome.NotDeletable or PublishingCancellationOutcome.Refused =>
                PublishingAttemptOutcome.Refused,
            PublishingCancellationOutcome.RateLimited => PublishingAttemptOutcome.RateLimited,
            PublishingCancellationOutcome.NotConfigured => PublishingAttemptOutcome.Unavailable,
            _ => PublishingAttemptOutcome.Unknown,
        };

        return new PublishingOutcomeWrite(
            record.Id,
            status,
            finishedAt,
            SubmittedAt: null,
            gone ? finishedAt : null,
            uncertain ? PublishingOperation.Cancel : null,
            uncertain ? startedAt : null,
            stillGoingOut ? record.ScheduledAt
                : gone ? finishedAt
                : finishedAt + Wait(result.RetryAfter, PublishingPolicy.UnavailableBackoff),

            // A clean removal has nothing to explain. Everything else says what happened, in this server's
            // own words, including "the provider no longer holds that post".
            result.FailureCategory,
            SummaryOf(result.FailureCategory),
            ProviderKey: string.Empty,
            ExternalUpdateId: null,
            new PublishingAttemptWrite(
                PublishingOperation.Cancel,
                outcome,
                startedAt,
                uncertain ? null : finishedAt,
                result.ProviderStatusCode is > 0 ? result.ProviderStatusCode : null,
                result.FailureCategory,
                result.FailureSummary,
                CorrelationId()),
            new AuditEntry(
                actorUserId,
                PublishingAuditActions.Cancelled,
                PublishingAuditActions.ResourceType,
                record.Id.ToString("D"),
                CorrelationId(),
                $"Removal of publication {record.Id:D} ended {result.Outcome}; the record is {status}"
                    + (uncertain ? " with its outcome unknown" : string.Empty),
                BeforeReference: $"status:{PublishingRecordStatus.Cancelling}",
                AfterReference: $"status:{status}"));
    }

    public async Task<PublishingRecordListServiceModel> ListForCardAsync(
        Guid contentCardId, CancellationToken cancellationToken)
    {
        // Nothing to ask the database: an empty id was never issued. The query filter does the rest, which is
        // why another workspace's card and a card that never existed give the same empty answer.
        var rows = contentCardId == Guid.Empty
            ? []
            : await dataLayer.ListForCardAsync(contentCardId, cancellationToken);

        return new PublishingRecordListServiceModel(
            [.. rows.Select(ToModel)], rows.Count < PublishingLedgerPolicy.RecordsPerCard);
    }

    /// <summary>The provider call and the write that records it. Nothing is open while the provider is asked.</summary>
    public async Task<PublishingRecordStatus> SubmitPendingAsync(
        PublishingReconcileTarget target, string providerKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        var result = await SubmitAsync(

            // No actor: the worker runs as the service, and whoever confirmed this is already named on the
            // record and in the confirmation's own audit entry.
            null,
            target.Row.Id,
            new PublishingTargetRow(
                target.Row.PublishingProfileId, target.Row.Platform, target.ProfileStatus, target.ChannelExternalId),
            providerKey,
            releaseLease: true,
            cancellationToken);

        return result.Succeeded ? result.Value!.Record.Status : target.Row.Status;
    }

    public async Task<PublishingRecordStatus> CancelClaimedAsync(
        PublishingReconcileTarget target, string externalUpdateId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        var result = await AskProviderToRemoveAsync(
            null, target.Row, externalUpdateId, releaseLease: true, cancellationToken);

        return result.Succeeded ? result.Value!.Status : target.Row.Status;
    }

    private async Task<OperationResult<PublishingScheduleOutcome>> SubmitAsync(
        string? actorUserId,
        Guid recordId,
        PublishingTargetRow target,
        string providerKey,
        bool releaseLease,
        CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;

        var marked = await dataLayer.MarkSubmittingAsync(
            recordId, startedAt, startedAt + PublishingPolicy.UnavailableBackoff, cancellationToken);

        if (marked.Row is null)
        {
            // Nothing was sent, so the same key may be sent again — which is why this is not a 404: the record
            // was committed a moment ago, and telling a caller it does not exist would be false.
            return Refused(new OperationError(
                PublishingErrorCodes.ScheduleUnavailable,
                "That publication was confirmed but could not be submitted just now. Try again with the same key.",
                new Dictionary<string, string[]>()));
        }

        // Another request, or a worker, already took this record into Submitting. Its record is the answer and
        // the provider is not asked again: the transition is the permission to call, and only one call gets it.
        if (!marked.Transitioned)
        {
            return OperationResult<PublishingScheduleOutcome>.Success(
                new PublishingScheduleOutcome(ToModel(marked.Row), true));
        }

        var row = marked.Row;

        PublishingSubmissionResult result;
        try
        {
            result = await dataLayer.SubmitAsync(
                new PublishingSubmission(target.ExternalId, row.ContentText, row.ScheduledAt), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The caller gave up while the provider was being asked. The post may exist, so the record is left
            // saying exactly that — and the attempt is written with a token that cannot be cancelled, because a
            // trail that disappears when a client disconnects is the trail that was needed most.
            await dataLayer.RecordOutcomeAsync(
                Next(
                    row,
                    PublishingSubmissionResult.Of(
                        PublishingSubmissionOutcome.Unknown,
                        "request_abandoned",
                        "The request was abandoned while the provider was being asked."),
                    startedAt,
                    clock.UtcNow,
                    providerKey,
                    actorUserId) with { ReleaseLease = releaseLease },
                CancellationToken.None);

            throw;
        }

        var finishedAt = clock.UtcNow;
        var write = Next(row, result, startedAt, finishedAt, providerKey, actorUserId) with
        {
            ReleaseLease = releaseLease,
        };

        // Not the caller's token: the provider has already been asked, and what it said must be committed even
        // if the client has stopped listening.
        var recorded = await dataLayer.RecordOutcomeAsync(write, CancellationToken.None);

        // The record is the answer whatever the provider said: a refusal, a throttle and a silence are all
        // states of a confirmed publication, not failures of the command that confirmed it.
        return OperationResult<PublishingScheduleOutcome>.Success(
            new PublishingScheduleOutcome(ToModel(recorded ?? row), false));
    }

    /// <summary>
    /// What one provider answer means for the record: the state machine, in one place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>An unknown outcome leaves the record saying so.</strong> It stays
    /// <see cref="PublishingRecordStatus.Submitting"/> with its uncertainty intact, because a post may exist
    /// at the provider and nothing here can tell. Reconciliation asks; this does not guess (13.10c).
    /// </para>
    /// <para>
    /// <strong>A retryable failure goes back to <see cref="PublishingRecordStatus.Pending"/></strong> with the
    /// provider left alone until <c>AvailableAt</c> — until the submit budget is spent, when it becomes
    /// something a person has to look at rather than a row that retries for ever.
    /// </para>
    /// </remarks>
    private PublishingOutcomeWrite Next(
        PublishingRecordRow record,
        PublishingSubmissionResult result,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        string providerKey,
        string? actorUserId)
    {
        var (status, outcome) = result.Outcome switch
        {
            PublishingSubmissionOutcome.Scheduled => (PublishingRecordStatus.Scheduled, PublishingAttemptOutcome.Accepted),
            PublishingSubmissionOutcome.Refused => (PublishingRecordStatus.Failed, PublishingAttemptOutcome.Refused),
            PublishingSubmissionOutcome.RateLimited => (Retryable(record), PublishingAttemptOutcome.RateLimited),
            PublishingSubmissionOutcome.Unavailable => (Retryable(record), PublishingAttemptOutcome.Unavailable),
            PublishingSubmissionOutcome.NotConfigured => (PublishingRecordStatus.Pending, PublishingAttemptOutcome.Unavailable),
            _ => (PublishingRecordStatus.Submitting, PublishingAttemptOutcome.Unknown),
        };

        var uncertain = result.Outcome is PublishingSubmissionOutcome.Unknown;

        var availableAt = result.Outcome switch
        {
            // Nothing is due until it is published. The reconciliation window starts from the scheduled time.
            PublishingSubmissionOutcome.Scheduled => record.ScheduledAt,
            PublishingSubmissionOutcome.Refused => finishedAt,
            PublishingSubmissionOutcome.RateLimited => finishedAt + Wait(result.RetryAfter, PublishingPolicy.RefusedBackoff),
            _ => finishedAt + Wait(result.RetryAfter, PublishingPolicy.UnavailableBackoff),
        };

        return new PublishingOutcomeWrite(
            record.Id,
            status,
            finishedAt,
            result.Outcome is PublishingSubmissionOutcome.Scheduled ? finishedAt : null,
            status is PublishingRecordStatus.Failed ? finishedAt : null,
            uncertain ? PublishingOperation.Submit : null,
            uncertain ? startedAt : null,
            availableAt,
            result.Outcome is PublishingSubmissionOutcome.Scheduled ? null : result.FailureCategory,
            result.Outcome is PublishingSubmissionOutcome.Scheduled ? null : SummaryOf(result.FailureCategory),
            providerKey,
            result.ExternalUpdateId,
            new PublishingAttemptWrite(
                PublishingOperation.Submit,
                outcome,
                startedAt,

                // A call that never came back has no completion time, which is what tells an attempt that
                // failed apart from one that was never answered.
                uncertain ? null : finishedAt,
                result.ProviderStatusCode is > 0 ? result.ProviderStatusCode : null,
                result.FailureCategory,
                result.FailureSummary,
                CorrelationId()),
            new AuditEntry(
                actorUserId,
                PublishingAuditActions.Submitted,
                PublishingAuditActions.ResourceType,
                record.Id.ToString("D"),
                CorrelationId(),
                $"Submit of publication {record.Id:D} ended {result.Outcome}; the record is {status}"
                    + (uncertain ? " with its outcome unknown" : string.Empty),
                BeforeReference: $"status:{PublishingRecordStatus.Submitting}",
                AfterReference: $"status:{status}"));
    }

    /// <summary>
    /// The sentence a creator reads, written here from the category rather than taken from the provider.
    /// </summary>
    /// <remarks>
    /// The gateway's own summary is this server's writing too, and it is kept on the attempt row. But a
    /// response is the one place a provider's prose must never be able to reach — it can quote the post, and
    /// through it the creator's own words — so what is returned is composed from a closed set of categories
    /// instead of copied from anything a provider influenced. A category this does not know gets a sentence
    /// that says only what is certain.
    /// </remarks>
    private static string? SummaryOf(string? category) => category switch
    {
        null => null,
        "rate_limited" => "The publishing provider is busy. This will be tried again.",
        "credential_rejected" => "The publishing provider refused this deployment's credential.",
        "provider_limit_reached" => "The publishing provider says this account has reached a limit.",
        "provider_rejected_input" => "The publishing provider would not accept this post as written.",
        "provider_refused" => "The publishing provider would not accept this post.",
        "provider_unavailable" => "The publishing provider could not take this post. This will be tried again.",
        "provider_timeout" or "provider_connection_lost" =>
            "The publishing provider did not answer, so whether this was published is not yet known.",
        "unreadable_answer" => "The publishing provider answered in a way this server could not read.",
        "provider_post_absent" => "The publishing provider no longer holds that post.",
        "provider_post_not_deletable" => "The publishing provider will not remove that post, so it still stands.",
        "provider_not_configured" => "This workspace has no publishing provider.",
        "request_abandoned" => "The request was abandoned, so this outcome is not yet known.",
        _ => "The publishing provider did not take this post.",
    };

    /// <summary>A failure worth another go — until the budget for them is spent.</summary>
    private static PublishingRecordStatus Retryable(PublishingRecordRow record) =>
        record.SubmitAttempts >= PublishingLedgerPolicy.MaxSubmitAttempts
            ? PublishingRecordStatus.NeedsAttention
            : PublishingRecordStatus.Pending;

    /// <summary>The provider's own wait when it stated a believable one, else this server's.</summary>
    private static TimeSpan Wait(TimeSpan? retryAfter, TimeSpan fallback) =>
        retryAfter is { } asked && asked > TimeSpan.Zero
            ? asked > PublishingPolicy.MaximumRetryAfter ? PublishingPolicy.MaximumRetryAfter : asked
            : fallback;

    /// <summary>
    /// The answer for a key this workspace already holds a record under: that record if it is the same
    /// request, a refusal if the key has been put to a different one, and null if the key is unused.
    /// </summary>
    /// <remarks>
    /// Consulted before every duplicate refusal, not only at the start. Two copies of one request can arrive
    /// close enough that the second passes the key check and then meets the first in the duplicate check — and
    /// being told "that is already going out" about your own retry is both confusing and wrong.
    /// </remarks>
    private async Task<OperationResult<PublishingScheduleOutcome>?> ReplayAsync(
        string key, SchedulePublishingViewModel model, CancellationToken cancellationToken)
    {
        if (await dataLayer.FindByKeyAsync(key, cancellationToken) is not { } existing)
        {
            return null;
        }

        return SameRequest(existing, model)
            ? OperationResult<PublishingScheduleOutcome>.Success(new PublishingScheduleOutcome(ToModel(existing), true))
            : Refused(new OperationError(
                IdempotencyPolicy.KeyReusedCode,
                "That idempotency key was already used for a different publication.",
                new Dictionary<string, string[]>()));
    }

    /// <summary>
    /// Whether a replayed key names the same request. Anything different means a second request wearing the
    /// first one's key, and answering it with the first one's record would report the wrong publication.
    /// </summary>
    private bool SameRequest(PublishingRecordRow existing, SchedulePublishingViewModel model) =>
        existing.ContentCardId == model.ContentCardId
        && existing.PublishingProfileId == model.PublishingProfileId
        && string.Equals(existing.ContentText, model.Text?.Trim(), StringComparison.Ordinal)
        && existing.MediaAssetId == model.MediaAssetId
        && existing.MediaAssetVersionNumber == model.MediaAssetVersionNumber
        && existing.ScheduledAt == model.ScheduledAt
        && existing.ConfirmedByMembershipId == workspace.MembershipId;

    private static PublishingRecordServiceModel ToModel(PublishingRecordRow row) => new(
        row.Id,
        row.ContentCardId,
        row.PublishingProfileId,
        row.Platform,
        row.Status,
        row.ContentText,
        row.MediaAssetId,
        row.MediaAssetVersionNumber,
        row.ScheduledAt,
        row.ScheduleTimeZoneId,
        row.ScheduledLocal,
        row.UncertainOperation is not null,
        row.FailureCategory,
        row.FailureSummary,
        row.SubmittedAt,
        row.CompletedAt,
        row.SubmitAttempts,
        row.CreatedAt,
        PublishingRecordConcurrencyToken.From(row.RowVersion));

    private static OperationResult<PublishingRecordServiceModel> NotFound() =>
        OperationResult<PublishingRecordServiceModel>.Failure(new OperationError(
            PublishingErrorCodes.RecordNotFound,
            "There is no publishing record with that id in this workspace.",
            new Dictionary<string, string[]>()));

    private static OperationResult<PublishingRecordServiceModel> Conflict(string code, string message) =>
        OperationResult<PublishingRecordServiceModel>.Failure(
            new OperationError(code, message, new Dictionary<string, string[]>()));

    private static OperationResult<PublishingScheduleOutcome> Invalid(params (string Field, string Error)[] errors) =>
        OperationResult<PublishingScheduleOutcome>.Failure(
            OperationError.Validation(PublishingErrorCodes.ScheduleInvalid, CannotSchedule, errors));

    private static OperationResult<PublishingScheduleOutcome> Refused(OperationError error) =>
        OperationResult<PublishingScheduleOutcome>.Failure(error);

    private static Guid CorrelationId()
    {
        // A W3C trace id is sixteen bytes, the same width as a Guid, so an operator can paste the audit row's
        // correlation id into a trace search and find the request.
        var traceId = System.Diagnostics.Activity.Current?.TraceId;

        return traceId is { } id && id != default ? Guid.ParseExact(id.ToHexString(), "N") : Guid.NewGuid();
    }
}
