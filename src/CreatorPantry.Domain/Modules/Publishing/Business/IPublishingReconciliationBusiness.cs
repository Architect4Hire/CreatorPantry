using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Publishing.Data;
using CreatorPantry.Domain.Modules.Publishing.Gateways;
using CreatorPantry.Domain.Modules.Publishing.Managers;

namespace CreatorPantry.Domain.Modules.Publishing.Business;

/// <summary>What one reconciliation pass over one profile's records did.</summary>
/// <param name="Settled">Records that reached a state nothing further is expected from.</param>
/// <param name="Submitted">Records that were due to be sent and were sent.</param>
/// <param name="StillWaiting">Records whose answer has not arrived yet; they are polled again later.</param>
/// <param name="NeedsAttention">Records that have been tried as often as they will be.</param>
/// <param name="Skipped">Records this deployment may not act on, or cannot find a provider mapping for.</param>
/// <param name="Unavailable">Records left untouched because the provider could not be read.</param>
public sealed record PublishingReconcileSummary(
    int Settled = 0,
    int Submitted = 0,
    int StillWaiting = 0,
    int NeedsAttention = 0,
    int Skipped = 0,
    int Unavailable = 0)
{
    public static PublishingReconcileSummary operator +(
        PublishingReconcileSummary left, PublishingReconcileSummary right) =>
        new(
            left.Settled + right.Settled,
            left.Submitted + right.Submitted,
            left.StillWaiting + right.StillWaiting,
            left.NeedsAttention + right.NeedsAttention,
            left.Skipped + right.Skipped,
            left.Unavailable + right.Unavailable);
}

/// <summary>
/// Settles publishing records whose outcome the provider has yet to confirm (PUB-002/003, INT-033).
/// </summary>
public interface IPublishingReconciliationBusiness
{
    /// <summary>
    /// Reconciles records that all publish to one profile, spending one listing on the lot of them.
    /// </summary>
    /// <remarks>
    /// Grouped by profile because the provider's quota is small and shared: one listing answers "did it go
    /// out", "does it exist" and "is it gone" for every record waiting on that channel.
    /// </remarks>
    Task<PublishingReconcileSummary> ReconcileProfileAsync(
        Guid publishingProfileId, IReadOnlyList<Guid> recordIds, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPublishingReconciliationBusiness"/>
/// <remarks>
/// <para>
/// <strong>Nothing is concluded from a listing that did not happen.</strong> A throttled or unreadable
/// listing leaves every record exactly as it was, with a backoff. Only a listing read to its end may be taken
/// as evidence that a post is absent, and even then only after <see cref="PublishingLedgerPolicy.AbsenceGrace"/>
/// — because "no post was ever made" is the one conclusion that can quietly lose a creator's work.
/// </para>
/// <para>
/// <strong>A record is never resubmitted on the strength of an absence.</strong> An unacknowledged submit that
/// cannot be found becomes <see cref="PublishingRecordStatus.Failed"/>, which a creator may confirm again
/// deliberately. The alternative — concluding "not created" and sending again — risks the audience seeing the
/// same post twice, and a stale or paged listing is exactly the condition in which that mistake would be made.
/// </para>
/// <para>
/// <strong>Only <see cref="PublishingRecordStatus.Pending"/> is ever sent</strong>, through the same seam and
/// the same transition gate a request uses. No path here submits a record that is already
/// <see cref="PublishingRecordStatus.Submitting"/>.
/// </para>
/// </remarks>
internal sealed class PublishingReconciliationBusiness(
    IPublishingRecordDataLayer dataLayer,
    IPublishingRecordBusiness records,
    IWorkspaceContext workspace,
    IClock clock) : IPublishingReconciliationBusiness
{
    public async Task<PublishingReconcileSummary> ReconcileProfileAsync(
        Guid publishingProfileId, IReadOnlyList<Guid> recordIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recordIds);

        var provider = dataLayer.DescribeProvider();

        // The credential belongs to one workspace. A record from any other is left alone entirely — not asked
        // about, not altered — which is the same rule the read and write paths follow (tenancy.md).
        if (provider is not { Configured: true, ProviderKey: not null, WorkspaceId: { } owner }
            || owner != workspace.WorkspaceId)
        {
            // Nothing is read or written about these records — but their claims are given up and pushed out,
            // or the next pass takes the same rows again and the owner's due work never reaches the batch.
            foreach (var recordId in recordIds)
            {
                await ParkIdAsync(recordId, cancellationToken);
            }

            return new PublishingReconcileSummary(Skipped: recordIds.Count);
        }

        var targets = new List<PublishingReconcileTarget>();
        var summary = new PublishingReconcileSummary();

        foreach (var recordId in recordIds)
        {
            // Under the workspace's own query filter: finding a row in the claim was not authorization, and
            // this is the read that is.
            var target = await dataLayer.FindReconcileTargetAsync(recordId, cancellationToken);

            // The second half of this is the grouping contract, enforced rather than assumed: one listing is
            // read for one channel, so a record of another profile must never be settled from it — an absence
            // in the wrong channel's listing would read as a post that was never made.
            if (target is not null && target.Row.PublishingProfileId == publishingProfileId)
            {
                targets.Add(target);
            }
            else
            {
                if (target is not null)
                {
                    await ParkAsync(target.Row, cancellationToken);
                }

                summary += new PublishingReconcileSummary(Skipped: 1);
            }
        }

        // The records due to be sent need no listing at all.
        foreach (var target in targets.Where(target => target.Row.Status is PublishingRecordStatus.Pending))
        {
            summary += await SubmitAsync(target, provider.ProviderKey, cancellationToken);
        }

        var waiting = targets
            .Where(target => target.Row.Status is not PublishingRecordStatus.Pending)
            .ToList();

        if (waiting.Count is 0)
        {
            return summary;
        }

        // One listing for every record on this channel.
        var listing = await dataLayer.ListPostsAsync(
            new PublishingPostLookup(waiting[0].ExternalAccountId, waiting[0].ChannelExternalId), cancellationToken);

        if (listing.Outcome is not PublishingPostLookupOutcome.Listed)
        {
            foreach (var target in waiting)
            {
                await RequeueAsync(target.Row, listing.RetryAfter, cancellationToken);
            }

            return summary + new PublishingReconcileSummary(Unavailable: waiting.Count);
        }

        foreach (var target in waiting)
        {
            summary += await SettleAsync(target, listing, provider.ProviderKey, cancellationToken);
        }

        return summary;
    }

    /// <summary>A record due to be sent. The same path a request takes, including the transition gate.</summary>
    private async Task<PublishingReconcileSummary> SubmitAsync(
        PublishingReconcileTarget target, string providerKey, CancellationToken cancellationToken)
    {
        // The profile has to be one the provider will still post to, exactly as at confirmation time.
        if (target.ProfileStatus is not PublishingProfileStatus.Ready)
        {
            await NeedsAttentionAsync(
                target,
                "provider_profile_not_ready",
                "The profile this was confirmed for can no longer be posted to.",
                cancellationToken);

            return new PublishingReconcileSummary(NeedsAttention: 1);
        }

        var result = await records.SubmitPendingAsync(target, providerKey, cancellationToken);

        return result switch
        {
            PublishingRecordStatus.Scheduled or PublishingRecordStatus.Published =>
                new PublishingReconcileSummary(Submitted: 1),
            PublishingRecordStatus.NeedsAttention => new PublishingReconcileSummary(NeedsAttention: 1),
            PublishingRecordStatus.Failed => new PublishingReconcileSummary(Settled: 1),
            _ => new PublishingReconcileSummary(StillWaiting: 1),
        };
    }

    /// <summary>What the listing says about one record, and what that makes of it.</summary>
    private async Task<PublishingReconcileSummary> SettleAsync(
        PublishingReconcileTarget target,
        PublishingPostLookupResult listing,
        string providerKey,
        CancellationToken cancellationToken)
    {
        var row = target.Row;
        var now = clock.UtcNow;
        var post = Match(target, listing);

        // Past the window, on a listing that was actually read to its end: a record nobody has settled in
        // this long is a person's to look at rather than a poll to run for ever — but a run of truncated
        // listings is not the provider failing to account for it.
        if (post is null && listing.Complete && now > row.ScheduledAt + PublishingLedgerPolicy.ReconcileWindow)
        {
            await NeedsAttentionAsync(
                target,
                "provider_outcome_unresolved",
                "The provider has not accounted for this publication, and it has been asked for long enough.",
                cancellationToken);

            return new PublishingReconcileSummary(NeedsAttention: 1);
        }

        if (post is { } found)
        {
            return await SettleFoundAsync(target, found, providerKey, cancellationToken);
        }

        // An absence only means something from a listing read to its end, and only after a grace period. What
        // the grace is measured from depends on what is missing, and the difference matters both ways.
        //
        // For a post that is still expected to go out — acknowledged, and not being cancelled — the grace runs
        // from its due time. Its row carries no uncertainty, so it becomes claimable exactly when it is due
        // and a confirmation is always older than that; measuring from the ask would give it no grace at all,
        // and a provider that moves a post through a state of its own at the moment it sends it would have a
        // live post recorded as never made.
        //
        // For everything else the grace runs from when the provider was last asked. A post it never
        // acknowledged is listed as soon as it exists, so a complete listing minutes later is good evidence —
        // and waiting for a due time days away would leave a creator believing something is going out. For a
        // cancellation, an absence is the answer that was wanted, so there is nothing to protect against.
        var asked = row.UncertainSince ?? row.CreatedAt;
        var waitingToGoOut = target.ExternalUpdateId is not null
            && row.Status is PublishingRecordStatus.Scheduled;

        var settledFrom = waitingToGoOut && row.ScheduledAt > asked ? row.ScheduledAt : asked;

        var provable = listing.Complete && now - settledFrom >= PublishingLedgerPolicy.AbsenceGrace;

        if (!provable)
        {
            await RequeueAsync(row, retryAfter: null, cancellationToken);

            return new PublishingReconcileSummary(StillWaiting: 1);
        }

        return await SettleAbsentAsync(target, cancellationToken);
    }

    /// <summary>The post is there. Its own state is now the record's.</summary>
    private async Task<PublishingReconcileSummary> SettleFoundAsync(
        PublishingReconcileTarget target,
        ProviderPost post,
        string providerKey,
        CancellationToken cancellationToken)
    {
        var row = target.Row;
        var now = clock.UtcNow;

        // A record that was trying to stop this post, and the post is still standing: ask again. Repeating a
        // removal cannot remove two posts, which is what makes this safe where resubmitting would not be.
        if (row.Status is PublishingRecordStatus.Cancelling && post.State is PublishingPostState.Scheduled)
        {
            if (row.CancelAttempts >= PublishingLedgerPolicy.MaxCancelAttempts)
            {
                await NeedsAttentionAsync(
                    target,
                    "provider_post_not_removed",
                    "The provider still holds this post after it was asked to remove it.",
                    cancellationToken);

                return new PublishingReconcileSummary(NeedsAttention: 1);
            }

            var removed = await records.CancelClaimedAsync(target, post.ExternalUpdateId, cancellationToken);

            return removed is PublishingRecordStatus.Cancelled
                ? new PublishingReconcileSummary(Settled: 1)
                : removed is PublishingRecordStatus.NeedsAttention
                    ? new PublishingReconcileSummary(NeedsAttention: 1)
                    : new PublishingReconcileSummary(StillWaiting: 1);
        }

        var (status, category, summary) = post.State switch
        {
            PublishingPostState.Sent when row.Status is PublishingRecordStatus.Cancelling =>
                (PublishingRecordStatus.Published, "cancellation_too_late",
                    "This went out before the provider removed it."),
            PublishingPostState.Sent => (PublishingRecordStatus.Published, null, (string?)null),
            PublishingPostState.Failed => (PublishingRecordStatus.Failed, "provider_delivery_failed",
                "The provider could not publish this."),
            _ => (PublishingRecordStatus.Scheduled, null, (string?)null),
        };

        var settled = status is not PublishingRecordStatus.Scheduled;

        await dataLayer.RecordOutcomeAsync(
            new PublishingOutcomeWrite(
                row.Id,
                status,
                now,

                // A post the provider holds was accepted by it, whether or not this server heard so at the time.
                SubmittedAt: row.SubmittedAt ?? now,
                settled ? post.DueAt ?? now : null,
                UncertainOperation: null,
                UncertainSince: null,
                settled ? now : now + PublishingLedgerPolicy.ScheduledPollInterval,
                category,
                summary,
                providerKey,

                // The id of a post this server may have made and never heard about. Written once; a record
                // that already has one keeps it.
                post.ExternalUpdateId,
                new PublishingAttemptWrite(
                    PublishingOperation.Reconcile,
                    PublishingAttemptOutcome.Accepted,
                    now,
                    now,
                    null,
                    category,
                    summary,
                    CorrelationId()),
                Audit(row, status, $"Reconciliation found the post for publication {row.Id:D}: it is {post.State}"),
                ReleaseLease: true),
            cancellationToken);

        return settled
            ? new PublishingReconcileSummary(Settled: 1)
            : new PublishingReconcileSummary(StillWaiting: 1);
    }

    /// <summary>The provider does not have it, and the listing is good enough to say so.</summary>
    private async Task<PublishingReconcileSummary> SettleAbsentAsync(
        PublishingReconcileTarget target, CancellationToken cancellationToken)
    {
        var row = target.Row;
        var now = clock.UtcNow;

        // A record with a post id that is no longer listed: somebody removed it at the provider, or the
        // cancellation this record was asking for worked. Either way it will not go out.
        var (status, category, summary) = target.ExternalUpdateId is not null
            ? (PublishingRecordStatus.Cancelled, "provider_post_absent",
                "The provider no longer holds that post.")

            // Never acknowledged. Recorded as failed and never resubmitted by anything: a creator confirms it
            // again if they still want it, and the duplicate guard allows that because failed is not live.
            : (PublishingRecordStatus.Failed, "provider_never_acknowledged",
                "The provider never acknowledged this post, so it was not published.");

        await dataLayer.RecordOutcomeAsync(
            new PublishingOutcomeWrite(
                row.Id,
                status,
                now,
                SubmittedAt: null,
                CompletedAt: now,
                UncertainOperation: null,
                UncertainSince: null,
                AvailableAt: now,
                category,
                summary,
                ProviderKey: string.Empty,
                ExternalUpdateId: null,
                new PublishingAttemptWrite(
                    PublishingOperation.Reconcile,
                    PublishingAttemptOutcome.Accepted,
                    now,
                    now,
                    null,
                    category,
                    summary,
                    CorrelationId()),
                Audit(row, status, $"Reconciliation found no post for publication {row.Id:D}"),
                ReleaseLease: true),
            cancellationToken);

        return new PublishingReconcileSummary(Settled: 1);
    }

    /// <summary>
    /// A record this pass may not act on: it keeps its state, gives up its claim, and is not looked at again
    /// for a while.
    /// </summary>
    /// <remarks>
    /// Without this a skipped record is re-claimed on every lease cycle, which both spins and fills the batch
    /// that the credential's own workspace is waiting for.
    /// </remarks>
    private Task ParkAsync(PublishingRecordRow row, CancellationToken cancellationToken) =>
        RequeueAsync(row, PublishingLedgerPolicy.ReconcileWindow, cancellationToken);

    /// <inheritdoc cref="ParkAsync"/>
    /// <remarks>
    /// By id, because this path has not read the row yet. The read is under the workspace's own filter — the
    /// worker resolved the workspace these records belong to before calling, so what cannot be found here is
    /// a record that is genuinely not this workspace's, and its claim lapses on its own.
    /// </remarks>
    private async Task ParkIdAsync(Guid recordId, CancellationToken cancellationToken)
    {
        if (await dataLayer.FindAsync(recordId, cancellationToken) is { } row)
        {
            await ParkAsync(row, cancellationToken);
        }
    }

    /// <summary>Nothing was learned. The record keeps its state, loses its claim, and is looked at later.</summary>
    private Task RequeueAsync(PublishingRecordRow row, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var wait = retryAfter is { } asked && asked > TimeSpan.Zero
            ? asked > PublishingPolicy.MaximumRetryAfter ? PublishingPolicy.MaximumRetryAfter : asked
            : PublishingLedgerPolicy.ScheduledPollInterval;

        return dataLayer.RecordOutcomeAsync(
            new PublishingOutcomeWrite(
                row.Id,
                row.Status,
                row.StatusChangedAt,
                SubmittedAt: null,
                row.CompletedAt,

                // Whatever was in doubt stays in doubt: a listing that did not happen settles nothing.
                row.UncertainOperation,
                row.UncertainSince,
                now + wait,
                row.FailureCategory,
                row.FailureSummary,
                ProviderKey: string.Empty,
                ExternalUpdateId: null,

                // No attempt row: a listing is not an attempt on this record, and one per poll would bury the
                // calls that actually did something.
                Attempt: null,
                Audit(row, row.Status, $"Reconciliation could not settle publication {row.Id:D} yet"),
                ReleaseLease: true),
            cancellationToken);
    }

    private Task NeedsAttentionAsync(
        PublishingReconcileTarget target, string category, string summary, CancellationToken cancellationToken)
    {
        var row = target.Row;
        var now = clock.UtcNow;

        return dataLayer.RecordOutcomeAsync(
            new PublishingOutcomeWrite(
                row.Id,
                PublishingRecordStatus.NeedsAttention,
                now,
                SubmittedAt: null,
                CompletedAt: null,

                // Kept: what a person has to look at is precisely what is still unknown.
                row.UncertainOperation,
                row.UncertainSince,
                now,
                category,
                summary,
                ProviderKey: string.Empty,
                ExternalUpdateId: null,
                Attempt: null,
                Audit(row, PublishingRecordStatus.NeedsAttention, $"Publication {row.Id:D} needs attention: {category}"),
                ReleaseLease: true),
            cancellationToken);
    }

    /// <summary>
    /// A post of this channel's that is this record's, by the only three things that can identify one.
    /// </summary>
    /// <remarks>
    /// The provider returns no identifier of ours, so a post this server made and never heard about is
    /// recognised by its text and its scheduled time. A record that already has an id uses that instead,
    /// which is exact.
    /// </remarks>
    private static ProviderPost? Match(PublishingReconcileTarget target, PublishingPostLookupResult listing)
    {
        if (target.ExternalUpdateId is { } known)
        {
            return listing.Posts.FirstOrDefault(post =>
                string.Equals(post.ExternalUpdateId, known, StringComparison.Ordinal));
        }

        return listing.Posts.FirstOrDefault(post =>
            string.Equals(post.Text, target.Row.ContentText, StringComparison.Ordinal)
            && post.DueAt is { } dueAt
            && (dueAt - target.Row.ScheduledAt).Duration() <= PublishingLedgerPolicy.DueAtMatchTolerance);
    }

    /// <summary>
    /// Audited with no actor: reconciliation runs as the service, long after whoever confirmed the
    /// publication, and naming them would say a person did this.
    /// </summary>
    private static AuditEntry Audit(PublishingRecordRow row, PublishingRecordStatus status, string summary) =>
        new(
            null,
            PublishingAuditActions.Reconciled,
            PublishingAuditActions.ResourceType,
            row.Id.ToString("D"),
            CorrelationId(),
            summary,
            BeforeReference: $"status:{row.Status}",
            AfterReference: $"status:{status}");

    private static Guid CorrelationId()
    {
        var traceId = System.Diagnostics.Activity.Current?.TraceId;

        return traceId is { } id && id != default ? Guid.ParseExact(id.ToHexString(), "N") : Guid.NewGuid();
    }
}
