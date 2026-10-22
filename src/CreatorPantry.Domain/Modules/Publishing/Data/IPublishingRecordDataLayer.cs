using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Limits;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Publishing.Data.Entities;
using CreatorPantry.Domain.Modules.Publishing.Gateways;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Publishing.Data;

/// <summary>One ledger row as everything above the data layer reads it. No provider identifier.</summary>
public sealed record PublishingRecordRow(
    Guid Id,
    Guid? ContentCardId,
    Guid PublishingProfileId,
    PublishingPlatform Platform,
    PublishingRecordStatus Status,
    string ContentText,
    Guid? MediaAssetId,
    int? MediaAssetVersionNumber,
    DateTimeOffset ScheduledAt,
    string ScheduleTimeZoneId,
    DateTime ScheduledLocal,
    DateTimeOffset StatusChangedAt,
    PublishingOperation? UncertainOperation,
    DateTimeOffset? UncertainSince,
    string? FailureCategory,
    string? FailureSummary,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? CompletedAt,
    int SubmitAttempts,
    int CancelAttempts,
    DateTimeOffset CreatedAt,
    Guid ConfirmedByMembershipId,
    string IdempotencyKey,
    byte[] RowVersion);

/// <summary>
/// Where a post is going, resolved once: the profile as this workspace knows it, and the provider's own id for
/// it from the integration record.
/// </summary>
/// <remarks>
/// The one place <see cref="ExternalId"/> is read from storage. Business passes it straight to the gateway
/// without keeping or returning it, and it reaches no ViewModel, no ServiceModel and no response — the only
/// row it is ever written to is <c>ExternalPublicationReference</c> (external.md).
/// </remarks>
public sealed record PublishingTargetRow(
    Guid ProfileId, PublishingPlatform Platform, PublishingProfileStatus Status, string ExternalId);

/// <summary>How a confirmation's first write ended.</summary>
public enum PublishingConfirmOutcome
{
    /// <summary>The pending row is committed, and so is the audit entry beside it.</summary>
    Created = 1,

    /// <summary>
    /// That idempotency key already names a record in this workspace, so nothing was written.
    /// </summary>
    /// <remarks>
    /// The durable half of "a retry publishes nothing twice": the unique index refuses the second row even
    /// when two requests arrive at once, or when the first arrived days ago (publishing.md).
    /// </remarks>
    KeyTaken = 2,

    /// <summary>
    /// This publication is already going out, and the store refused the second row.
    /// </summary>
    /// <remarks>
    /// The race the duplicate check cannot win on its own: two confirmations with two different keys both
    /// pass the question and both try to insert. The unique index is what makes the second one impossible
    /// rather than merely unlikely.
    /// </remarks>
    DuplicateLive = 3,

    /// <summary>The store refused the row for some other reason. Nothing was written and nothing was sent.</summary>
    Refused = 4,
}

/// <summary>
/// The outcome of trying to take a record into <see cref="PublishingRecordStatus.Submitting"/>.
/// </summary>
/// <remarks>
/// <paramref name="Transitioned"/> is the permission to call the provider, and only this call gets it: a row
/// that was already being submitted comes back with the flag clear, which is what stops two requests — or a
/// request and a worker — each making a post.
/// </remarks>
public sealed record PublishingMarkResult(PublishingRecordRow? Row, bool Transitioned);

/// <summary>One attempt to record beside the state it produced. Outcomes only; never a provider's message.</summary>
public sealed record PublishingAttemptWrite(
    PublishingOperation Operation,
    PublishingAttemptOutcome Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int? ProviderStatusCode,
    string? FailureCategory,
    string? FailureSummary,
    Guid CorrelationId);

/// <summary>
/// The whole of what one operation on a record changed, as Business decided it.
/// </summary>
/// <remarks>
/// <para>
/// One type for a submit, a cancellation and a reconciliation, because the three write the same columns and
/// differ only in which of them they have anything to say about. Every state field is given rather than
/// computed here: which status a provider's answer means is a domain decision, and a data layer that worked
/// it out would be deciding the state machine (backend.md).
/// </para>
/// <para>
/// <paramref name="Attempt"/> is null when the provider was never asked — a publication cancelled before it
/// was sent has no call to show, and inventing one would put something in the trail that never happened.
/// </para>
/// </remarks>
/// <param name="ExternalUpdateId">
/// The provider's id for the post, when this operation learned one — from a submit that was accepted, or from
/// a reconciliation that found the post a lost answer had made. Ignored when the record already has one:
/// learning the same post twice is not a second post.
/// </param>
/// <param name="ReleaseLease">
/// True to clear the record's claim, which is what a worker's write does and a request's does not.
/// </param>
public sealed record PublishingOutcomeWrite(
    Guid RecordId,
    PublishingRecordStatus Status,
    DateTimeOffset StatusChangedAt,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? CompletedAt,
    PublishingOperation? UncertainOperation,
    DateTimeOffset? UncertainSince,
    DateTimeOffset AvailableAt,
    string? FailureCategory,
    string? FailureSummary,
    string ProviderKey,
    string? ExternalUpdateId,
    PublishingAttemptWrite? Attempt,
    AuditEntry Audit,
    bool ReleaseLease = false);

/// <summary>
/// Where a record stands with its provider, as a reconciliation needs it: the row, the provider's ids for the
/// channel and the account that holds it, and the post's id when one was ever learned.
/// </summary>
/// <remarks>
/// The provider values go to the gateway and no further; nothing above Business holds them, and none reaches a
/// ServiceModel or a response (external.md).
/// </remarks>
public sealed record PublishingReconcileTarget(
    PublishingRecordRow Row,
    PublishingProfileStatus ProfileStatus,
    string ChannelExternalId,
    string ExternalAccountId,
    string? ExternalUpdateId);

/// <summary>EF access for the publishing ledger, its provider mappings and its attempt trail.</summary>
public interface IPublishingRecordRepository
{
    /// <summary>The row with this id, or null. Read-only.</summary>
    Task<PublishingRecordRow?> FindRowAsync(Guid recordId, CancellationToken cancellationToken);

    /// <summary>The row this workspace already has under that idempotency key, or null. Read-only.</summary>
    Task<PublishingRecordRow?> FindRowByKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// How many publications this workspace has confirmed since <paramref name="since"/> (15.3), whatever became of
    /// them. Inside the workspace filter, so no other workspace's ledger is ever in the count.
    /// </summary>
    Task<int> CountConfirmedSinceAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>
    /// Where <paramref name="recordId"/> falls in its own UTC day's confirmations, counting itself. Null when the
    /// record is not this workspace's.
    /// </summary>
    Task<int?> ConfirmedThroughAsync(Guid recordId, CancellationToken cancellationToken);

    /// <summary>
    /// One card's publications, newest scheduled first, capped at
    /// <see cref="PublishingLedgerPolicy.RecordsPerCard"/>.
    /// </summary>
    /// <remarks>
    /// Capped rather than paged: a card is one piece of work, and a creator who has confirmed more
    /// publications for it than this has a different problem than a missing page. The cap is stated in the
    /// route's own documentation so a client is never silently shown a partial list.
    /// </remarks>
    Task<IReadOnlyList<PublishingRecordRow>> ListForCardAsync(Guid contentCardId, CancellationToken cancellationToken);

    /// <summary>Tracked, for the write that follows a provider call.</summary>
    Task<PublishingRecord?> FindForUpdateAsync(Guid recordId, CancellationToken cancellationToken);

    /// <summary>The profile and the provider's id for it, when this workspace has both.</summary>
    Task<PublishingTargetRow?> FindTargetAsync(Guid profileId, string providerKey, CancellationToken cancellationToken);

    /// <summary>Whether this record already has a provider post mapped to it.</summary>
    Task<bool> HasExternalReferenceAsync(Guid recordId, CancellationToken cancellationToken);

    /// <summary>
    /// A record of this workspace that is already going out to this profile at this time — or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guard against the same publication being confirmed twice under two different idempotency keys,
    /// which the key's own index cannot catch. Only live states count: a failed or cancelled publication is
    /// something a creator may legitimately confirm again.
    /// </para>
    /// <para>
    /// <strong>The text is not part of the question</strong>, deliberately: the index behind this
    /// (<c>UX_PublishingRecords_Live_Card_Profile_Time</c>) cannot span 20,000 characters, and a check that
    /// asked more than the index enforces would be a check that passes where the insert then fails. One live
    /// publication per card, profile and time is the rule both of them hold.
    /// </para>
    /// </remarks>
    Task<Guid?> FindLiveDuplicateAsync(
        Guid? contentCardId,
        Guid publishingProfileId,
        string contentText,
        DateTimeOffset scheduledAt,
        CancellationToken cancellationToken);

    /// <summary>The provider's id for this record's post, when one was ever learned.</summary>
    Task<string?> FindExternalUpdateIdAsync(Guid recordId, CancellationToken cancellationToken);

    /// <summary>
    /// The record with everything a reconciliation needs to ask the provider about it, or null when the
    /// workspace has no such record — or no provider mapping for its profile.
    /// </summary>
    Task<PublishingReconcileTarget?> FindReconcileTargetAsync(Guid recordId, CancellationToken cancellationToken);

    /// <summary>The next number in this record's attempt trail, from one.</summary>
    Task<int> NextAttemptNumberAsync(Guid recordId, CancellationToken cancellationToken);

    void Add(PublishingRecord record);

    void Add(ExternalPublicationReference reference);

    void Add(PublishingDeliveryAttempt attempt);
}

internal sealed class PublishingRecordRepository(CreatorPantryDbContext context) : IPublishingRecordRepository
{
    public Task<PublishingRecordRow?> FindRowAsync(Guid recordId, CancellationToken cancellationToken) =>
        Rows(context.PublishingRecords.AsNoTracking().Where(record => record.Id == recordId))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<int> CountConfirmedSinceAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        context.PublishingRecords.CountAsync(record => record.CreatedAt >= since, cancellationToken);

    public async Task<int?> ConfirmedThroughAsync(Guid recordId, CancellationToken cancellationToken)
    {
        var createdAt = await context.PublishingRecords
            .Where(record => record.Id == recordId)
            .Select(record => (DateTimeOffset?)record.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (createdAt is not { } instant)
        {
            return null;
        }

        var dayStart = LimitDay.StartOf(instant);

        return await context.PublishingRecords.CountAsync(
            record => record.CreatedAt >= dayStart && record.CreatedAt <= instant, cancellationToken);
    }

    public Task<PublishingRecordRow?> FindRowByKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        Rows(context.PublishingRecords.AsNoTracking().Where(record => record.IdempotencyKey == idempotencyKey))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<PublishingRecordRow>> ListForCardAsync(
        Guid contentCardId, CancellationToken cancellationToken) =>
        await Rows(context.PublishingRecords.AsNoTracking()
                .Where(record => record.ContentCardId == contentCardId)
                .OrderByDescending(record => record.ScheduledAt)
                .ThenByDescending(record => record.CreatedAt)
                .Take(PublishingLedgerPolicy.RecordsPerCard))
            .ToListAsync(cancellationToken);

    public Task<PublishingRecord?> FindForUpdateAsync(Guid recordId, CancellationToken cancellationToken) =>
        context.PublishingRecords.FirstOrDefaultAsync(record => record.Id == recordId, cancellationToken);

    public Task<PublishingTargetRow?> FindTargetAsync(
        Guid profileId, string providerKey, CancellationToken cancellationToken) =>
        context.PublishingProfiles.AsNoTracking()
            .Where(profile => profile.Id == profileId && profile.ProviderKey == providerKey)
            .Join(
                context.ExternalPublishingProfileReferences.AsNoTracking(),
                profile => profile.Id,
                reference => reference.PublishingProfileId,
                (profile, reference) => new PublishingTargetRow(
                    profile.Id, profile.Platform, profile.Status, reference.ExternalId))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<bool> HasExternalReferenceAsync(Guid recordId, CancellationToken cancellationToken) =>
        context.ExternalPublicationReferences.AsNoTracking()
            .AnyAsync(reference => reference.PublishingRecordId == recordId, cancellationToken);

    public async Task<Guid?> FindLiveDuplicateAsync(
        Guid? contentCardId,
        Guid publishingProfileId,
        string contentText,
        DateTimeOffset scheduledAt,
        CancellationToken cancellationToken) =>
        await context.PublishingRecords.AsNoTracking()
            .Where(record => record.ContentCardId == contentCardId
                && record.PublishingProfileId == publishingProfileId
                && record.ScheduledAt == scheduledAt
                && (record.Status == PublishingRecordStatus.Pending
                    || record.Status == PublishingRecordStatus.Submitting
                    || record.Status == PublishingRecordStatus.Scheduled
                    || record.Status == PublishingRecordStatus.Cancelling
                    || record.Status == PublishingRecordStatus.NeedsAttention))
            .Select(record => (Guid?)record.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<string?> FindExternalUpdateIdAsync(Guid recordId, CancellationToken cancellationToken) =>
        context.ExternalPublicationReferences.AsNoTracking()
            .Where(reference => reference.PublishingRecordId == recordId)
            .Select(reference => reference.ExternalUpdateId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<PublishingReconcileTarget?> FindReconcileTargetAsync(
        Guid recordId, CancellationToken cancellationToken)
    {
        // The row first, under the workspace's own query filter — this is the read that authorizes, after a
        // claim that did not.
        var row = await FindRowAsync(recordId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        // Then where it goes. Two reads rather than one join, because a join whose key comes from a projected
        // row is not something EF can translate.
        var destination = await context.PublishingProfiles.AsNoTracking()
            .Where(profile => profile.Id == row.PublishingProfileId)
            .Join(
                context.ExternalPublishingProfileReferences.AsNoTracking(),
                profile => profile.Id,
                reference => reference.PublishingProfileId,
                (profile, reference) => new
                {
                    profile.Status,
                    reference.ExternalId,
                    reference.ExternalAccountId,
                })
            .FirstOrDefaultAsync(cancellationToken);

        return destination is null
            ? null
            : new PublishingReconcileTarget(
                row,
                destination.Status,
                destination.ExternalId,
                destination.ExternalAccountId,
                await FindExternalUpdateIdAsync(recordId, cancellationToken));
    }

    public async Task<int> NextAttemptNumberAsync(Guid recordId, CancellationToken cancellationToken) =>
        await context.PublishingDeliveryAttempts.AsNoTracking()
            .Where(attempt => attempt.PublishingRecordId == recordId)
            .MaxAsync(attempt => (int?)attempt.AttemptNumber, cancellationToken) + 1 ?? 1;

    public void Add(PublishingRecord record) => context.PublishingRecords.Add(record);

    public void Add(ExternalPublicationReference reference) => context.ExternalPublicationReferences.Add(reference);

    public void Add(PublishingDeliveryAttempt attempt) => context.PublishingDeliveryAttempts.Add(attempt);

    /// <summary>
    /// The projection, applied after the caller's own filter: a <c>Where</c> over a constructed record is not
    /// something EF can translate.
    /// </summary>
    private static IQueryable<PublishingRecordRow> Rows(IQueryable<PublishingRecord> records) =>
        records
            .Select(record => new PublishingRecordRow(
                record.Id,
                record.ContentCardId,
                record.PublishingProfileId,
                record.Platform,
                record.Status,
                record.ContentText,
                record.MediaAssetId,
                record.MediaAssetVersionNumber,
                record.ScheduledAt,
                record.ScheduleTimeZoneId,
                record.ScheduledLocal,
                record.StatusChangedAt,
                record.UncertainOperation,
                record.UncertainSince,
                record.FailureCategory,
                record.FailureSummary,
                record.SubmittedAt,
                record.CompletedAt,
                record.SubmitAttempts,
                record.CancelAttempts,
                record.CreatedAt,
                record.ConfirmedByMembershipId,
                record.IdempotencyKey,
                record.RowVersion));
}

/// <summary>
/// The persistence and provider steps one confirmation takes, each a complete operation of its own.
/// </summary>
/// <remarks>
/// <strong>Three calls rather than one, because no transaction may span the provider call.</strong> A commit
/// held open around <c>createPost</c> would hold a database write for as long as a stranger's server takes to
/// answer, and a rollback after a successful call would erase the only record that a post exists. So the row
/// is committed first, the provider is called with nothing open, and what it said is committed second — and
/// every step in between is a state the ledger can be found in and recovered from (publishing.md).
/// </remarks>
public interface IPublishingRecordDataLayer
{
    /// <inheritdoc cref="IPublishingPostGateway.Describe"/>
    PublishingProviderDescriptor DescribeProvider();

    /// <inheritdoc cref="IPublishingRecordRepository.FindRowAsync"/>
    Task<PublishingRecordRow?> FindAsync(Guid recordId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IPublishingRecordRepository.FindRowByKeyAsync"/>
    Task<PublishingRecordRow?> FindByKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <inheritdoc cref="IPublishingRecordRepository.CountConfirmedSinceAsync"/>
    Task<int> CountConfirmedSinceAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <inheritdoc cref="IPublishingRecordRepository.ConfirmedThroughAsync"/>
    Task<int?> ConfirmedThroughAsync(Guid recordId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IPublishingRecordRepository.ListForCardAsync"/>
    Task<IReadOnlyList<PublishingRecordRow>> ListForCardAsync(Guid contentCardId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IPublishingRecordRepository.FindTargetAsync"/>
    Task<PublishingTargetRow?> FindTargetAsync(Guid profileId, string providerKey, CancellationToken cancellationToken);

    /// <inheritdoc cref="IPublishingRecordRepository.FindLiveDuplicateAsync"/>
    Task<Guid?> FindLiveDuplicateAsync(
        Guid? contentCardId,
        Guid publishingProfileId,
        string contentText,
        DateTimeOffset scheduledAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Commits the pending row and the audit entry of the confirmation, in one transaction, before anything is
    /// sent anywhere.
    /// </summary>
    Task<PublishingConfirmOutcome> ConfirmAsync(
        PublishingRecord record, AuditEntry audit, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the record as being submitted now, and its outcome as not yet known.
    /// </summary>
    /// <remarks>
    /// Its own commit, and the reason the ledger can survive a process dying mid-call: from here on the row
    /// says a write to the provider was begun, so nothing retries it blindly. Returns null when the record is
    /// not in a state a submit may begin from, which is how two requests racing the same record resolve.
    /// </remarks>
    Task<PublishingMarkResult> MarkSubmittingAsync(
        Guid recordId, DateTimeOffset now, DateTimeOffset availableAt, CancellationToken cancellationToken);

    /// <summary>The provider call. No transaction is open, and this is the only step that is not a write.</summary>
    Task<PublishingSubmissionResult> SubmitAsync(PublishingSubmission submission, CancellationToken cancellationToken);

    /// <summary>
    /// Commits what an operation did: the record's new state, its attempt row when there was a call, the
    /// provider's mapping record when one was learned, and the audit entry — in one transaction.
    /// </summary>
    Task<PublishingRecordRow?> RecordOutcomeAsync(PublishingOutcomeWrite write, CancellationToken cancellationToken);

    /// <inheritdoc cref="IPublishingRecordRepository.FindExternalUpdateIdAsync"/>
    Task<string?> FindExternalUpdateIdAsync(Guid recordId, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the record as being cancelled now, and its outcome as not yet known.
    /// </summary>
    /// <remarks>
    /// The same commit-before-the-call the submit makes, and for the same reason. Unlike the submit's, the
    /// transition may be re-entered: a record already <see cref="PublishingRecordStatus.Cancelling"/> is
    /// allowed to ask again, because asking twice cannot remove two posts and is how an unknown removal
    /// settles.
    /// </remarks>
    Task<PublishingMarkResult> MarkCancellingAsync(
        Guid recordId,
        DateTimeOffset now,
        DateTimeOffset availableAt,
        AuditEntry audit,
        CancellationToken cancellationToken);

    /// <summary>The provider call. No transaction is open.</summary>
    Task<PublishingCancellationResult> CancelAsync(
        PublishingCancellation cancellation, CancellationToken cancellationToken);

    /// <inheritdoc cref="IPublishingRecordRepository.FindReconcileTargetAsync"/>
    Task<PublishingReconcileTarget?> FindReconcileTargetAsync(Guid recordId, CancellationToken cancellationToken);

    /// <summary>Lists what the provider holds for one channel. Read-only, and one listing settles many records.</summary>
    Task<PublishingPostLookupResult> ListPostsAsync(
        PublishingPostLookup lookup, CancellationToken cancellationToken);
}

internal sealed class PublishingRecordDataLayer(
    IPublishingRecordRepository repository,
    IPublishingPostGateway gateway,
    IAuditWriter auditWriter,
    CreatorPantryDbContext db,
    ILogger<PublishingRecordDataLayer> logger) : IPublishingRecordDataLayer
{
    public PublishingProviderDescriptor DescribeProvider() => gateway.Describe();

    public Task<PublishingRecordRow?> FindAsync(Guid recordId, CancellationToken cancellationToken) =>
        repository.FindRowAsync(recordId, cancellationToken);

    public Task<PublishingRecordRow?> FindByKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        repository.FindRowByKeyAsync(idempotencyKey, cancellationToken);

    public Task<int> CountConfirmedSinceAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        repository.CountConfirmedSinceAsync(since, cancellationToken);

    public Task<int?> ConfirmedThroughAsync(Guid recordId, CancellationToken cancellationToken) =>
        repository.ConfirmedThroughAsync(recordId, cancellationToken);

    public Task<IReadOnlyList<PublishingRecordRow>> ListForCardAsync(
        Guid contentCardId, CancellationToken cancellationToken) =>
        repository.ListForCardAsync(contentCardId, cancellationToken);

    public Task<PublishingTargetRow?> FindTargetAsync(
        Guid profileId, string providerKey, CancellationToken cancellationToken) =>
        repository.FindTargetAsync(profileId, providerKey, cancellationToken);

    public Task<Guid?> FindLiveDuplicateAsync(
        Guid? contentCardId,
        Guid publishingProfileId,
        string contentText,
        DateTimeOffset scheduledAt,
        CancellationToken cancellationToken) =>
        repository.FindLiveDuplicateAsync(
            contentCardId, publishingProfileId, contentText, scheduledAt, cancellationToken);

    public async Task<PublishingConfirmOutcome> ConfirmAsync(
        PublishingRecord record, AuditEntry audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(audit);

        repository.Add(record);
        auditWriter.Record(audit);

        try
        {
            await db.SaveChangesAsync(cancellationToken);

            return PublishingConfirmOutcome.Created;
        }
        catch (DbUpdateException exception)
        {
            // Nothing of this one is left staged, whichever way it failed.
            db.ChangeTracker.Clear();

            // Which refusal it was is answered by looking, not by reading a message: the key being taken is
            // the expected one, and it is also the one that must not be reported as an error.
            if (await repository.FindRowByKeyAsync(record.IdempotencyKey, cancellationToken) is not null)
            {
                return PublishingConfirmOutcome.KeyTaken;
            }

            // The other refusal that is expected rather than exceptional: something with this card, profile
            // and time is already going out, and the index said so.
            if (await repository.FindLiveDuplicateAsync(
                    record.ContentCardId,
                    record.PublishingProfileId,
                    record.ContentText,
                    record.ScheduledAt,
                    cancellationToken) is not null)
            {
                return PublishingConfirmOutcome.DuplicateLive;
            }

            // By type alone: an inner message can quote the content back.
            logger.LogWarning(
                "A publishing confirmation was not committed ({ExceptionType}).",
                exception.InnerException?.GetType().Name ?? exception.GetType().Name);

            return PublishingConfirmOutcome.Refused;
        }
    }

    public async Task<PublishingMarkResult> MarkSubmittingAsync(
        Guid recordId, DateTimeOffset now, DateTimeOffset availableAt, CancellationToken cancellationToken)
    {
        var record = await repository.FindForUpdateAsync(recordId, cancellationToken);

        // Pending is the only state a fresh submit may begin from. Anything else means another request, or a
        // worker, got there first — and a second createPost is the one thing that must never happen.
        if (record is null || record.Status is not PublishingRecordStatus.Pending)
        {
            return new PublishingMarkResult(
                record is null ? null : await repository.FindRowAsync(recordId, cancellationToken), false);
        }

        record.Status = PublishingRecordStatus.Submitting;
        record.StatusChangedAt = now;
        record.SubmitAttempts += 1;
        record.LastAttemptAt = now;
        record.AvailableAt = availableAt;

        // Committed before the call, not after it: from this instant the row says an outcome is unknown, so a
        // process that dies mid-call leaves a record that reconciliation can resolve rather than one that
        // claims nothing was sent.
        record.UncertainOperation = PublishingOperation.Submit;
        record.UncertainSince = now;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another connection moved this row between the read above and this write. Losing that race is an
            // ordinary outcome, not a failure: the winner holds the claim, and the caller is told it does not
            // — which is what stops it calling the provider.
            db.ChangeTracker.Clear();

            return new PublishingMarkResult(await repository.FindRowAsync(recordId, cancellationToken), false);
        }

        return new PublishingMarkResult(await repository.FindRowAsync(recordId, cancellationToken), true);
    }

    public Task<PublishingSubmissionResult> SubmitAsync(
        PublishingSubmission submission, CancellationToken cancellationToken) =>
        gateway.SubmitAsync(submission, cancellationToken);

    public Task<string?> FindExternalUpdateIdAsync(Guid recordId, CancellationToken cancellationToken) =>
        repository.FindExternalUpdateIdAsync(recordId, cancellationToken);

    public async Task<PublishingMarkResult> MarkCancellingAsync(
        Guid recordId,
        DateTimeOffset now,
        DateTimeOffset availableAt,
        AuditEntry audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audit);

        var record = await repository.FindForUpdateAsync(recordId, cancellationToken);

        // The states a removal may be asked for from. Cancelling is among them on purpose: a cancellation
        // whose outcome nobody knows is settled by asking again, not by a worker being the only one allowed to.
        if (record is null
            || record.Status is not (PublishingRecordStatus.Scheduled or PublishingRecordStatus.Cancelling
                or PublishingRecordStatus.NeedsAttention))
        {
            return new PublishingMarkResult(
                record is null ? null : await repository.FindRowAsync(recordId, cancellationToken), false);
        }

        record.Status = PublishingRecordStatus.Cancelling;
        record.StatusChangedAt = now;
        record.CancelAttempts += 1;
        record.LastAttemptAt = now;
        record.AvailableAt = availableAt;

        // Committed before the call: from here the row says a removal was begun and its outcome is unknown, so
        // a process that dies mid-call leaves something recoverable rather than a record claiming the post is
        // gone.
        record.UncertainOperation = PublishingOperation.Cancel;
        record.UncertainSince = now;

        // The asking is the audited act, whatever becomes of it — so it commits with the state change that
        // begins it, not with the outcome that may never arrive.
        auditWriter.Record(audit);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another connection moved this row between the read above and this write. Losing that race is an
            // ordinary outcome, not a failure: the winner holds the claim, and the caller is told it does not
            // — which is what stops it calling the provider.
            db.ChangeTracker.Clear();

            return new PublishingMarkResult(await repository.FindRowAsync(recordId, cancellationToken), false);
        }

        return new PublishingMarkResult(await repository.FindRowAsync(recordId, cancellationToken), true);
    }

    public Task<PublishingCancellationResult> CancelAsync(
        PublishingCancellation cancellation, CancellationToken cancellationToken) =>
        gateway.CancelAsync(cancellation, cancellationToken);

    public async Task<PublishingRecordRow?> RecordOutcomeAsync(
        PublishingOutcomeWrite write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);

        var record = await repository.FindForUpdateAsync(write.RecordId, cancellationToken);
        if (record is null)
        {
            return null;
        }

        record.Status = write.Status;
        record.StatusChangedAt = write.StatusChangedAt;
        record.SubmittedAt = write.SubmittedAt ?? record.SubmittedAt;
        record.CompletedAt = write.CompletedAt;
        record.UncertainOperation = write.UncertainOperation;
        record.UncertainSince = write.UncertainSince;
        record.AvailableAt = write.AvailableAt;
        record.FailureCategory = write.FailureCategory;
        record.FailureSummary = write.FailureSummary;

        if (write.ReleaseLease)
        {
            record.LeasedBy = null;
            record.LeaseExpiresAt = null;
        }

        if (write.Attempt is { } attempt)
        {
            repository.Add(new PublishingDeliveryAttempt
            {
                Id = Guid.NewGuid(),
                PublishingRecordId = write.RecordId,
                AttemptNumber = await repository.NextAttemptNumberAsync(write.RecordId, cancellationToken),
                Operation = attempt.Operation,
                Outcome = attempt.Outcome,
                StartedAt = attempt.StartedAt,
                CompletedAt = attempt.CompletedAt,
                ProviderStatusCode = attempt.ProviderStatusCode,
                FailureCategory = attempt.FailureCategory,
                FailureSummary = attempt.FailureSummary,
                CorrelationId = attempt.CorrelationId,
            });
        }

        // The provider's id for the post, the only row it is ever written to. Skipped when one is already
        // there: a reconciliation that finds the post a lost answer created must not make a second mapping.
        if (write.ExternalUpdateId is { Length: > 0 } externalId
            && !await repository.HasExternalReferenceAsync(write.RecordId, cancellationToken))
        {
            repository.Add(new ExternalPublicationReference
            {
                Id = Guid.NewGuid(),
                PublishingRecordId = write.RecordId,
                ProviderKey = write.ProviderKey,
                ExternalUpdateId = externalId,
                FirstSeenAt = write.StatusChangedAt,
            });
        }

        auditWriter.Record(write.Audit);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // The provider may already hold the post, and the row still says the outcome is unknown — which
            // is the state reconciliation is built for. Logged by exception type alone: a store's own message
            // for a unique-index violation quotes the key, and that key is the provider's id for the post.
            logger.LogWarning(
                "A publishing outcome was not committed ({ExceptionType}).",
                exception.InnerException?.GetType().Name ?? exception.GetType().Name);

            db.ChangeTracker.Clear();

            return null;
        }

        return await repository.FindRowAsync(write.RecordId, cancellationToken);
    }

    public Task<PublishingReconcileTarget?> FindReconcileTargetAsync(
        Guid recordId, CancellationToken cancellationToken) =>
        repository.FindReconcileTargetAsync(recordId, cancellationToken);

    public Task<PublishingPostLookupResult> ListPostsAsync(
        PublishingPostLookup lookup, CancellationToken cancellationToken) =>
        gateway.ListPostsAsync(lookup, cancellationToken);
}
