using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Publishing.Data;

/// <summary>
/// What a worker learns by claiming a publishing record: where to look, whose it is, which profile it goes to,
/// what state it is in, and under which lease. Nothing about what is being published.
/// </summary>
/// <remarks>
/// <paramref name="PublishingProfileId"/> is CreatorPantry's own id, never a provider's — it is here so a pass
/// can group the records waiting on one channel and settle them with a single listing.
/// </remarks>
public sealed record PublishingReconciliationClaim(
    Guid RecordId,
    Guid WorkspaceId,
    Guid PublishingProfileId,
    PublishingRecordStatus Status,
    Guid LeaseToken);

/// <summary>
/// The cross-workspace reads the publishing ledger's background work needs: claiming the next records with
/// something outstanding, and recovering claims whose worker never came back.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This file is a documented carve-out from tenancy.md's <c>IgnoreQueryFilters</c> prohibition</strong>,
/// and it carries the same two conditions as the five claim repositories before it. A worker looks for due
/// work before it knows whose it is, so no <c>IWorkspaceContext</c> exists to filter by.
/// </para>
/// <para>
/// <strong>Identifiers and queue state only.</strong> Every query here projects to ids, a status and a lease
/// before it materializes anything: no <c>PublishingRecord</c> entity is loaded, so the text of a post never
/// enters this process from a workspace nobody has resolved. The claim's consumer resolves and validates that
/// workspace through <c>IWorkspaceResolutionFacade</c> before reading anything else — finding a row is not
/// authorization, and every read of creator content that follows is made under the workspace's own filter.
/// </para>
/// <para>
/// <strong>Queue state is all these writes touch:</strong> status, attempts, lease and availability. No
/// <c>SetProperty</c> here names <c>WorkspaceId</c>, and none ever may. The file is listed in
/// <c>BulkOperationBoundaryTests.Exemptions</c>, which is what makes granting the exception cost a reviewable
/// line — and anything added to this file inherits the carve-out silently, so it has to meet the same two
/// conditions.
/// </para>
/// <para>
/// <strong>Why a conditional update rather than load-and-save.</strong> The <c>WHERE</c> is the race and the
/// affected-row count is who won. A publishing record does carry a <c>RowVersion</c>, but a load-then-save
/// claim would still read the row — and reading the row means reading the post's text in a process that has
/// not resolved the workspace yet.
/// </para>
/// </remarks>
internal sealed class PublishingReconciliationClaimRepository(CreatorPantryDbContext context)
{
    /// <summary>
    /// The statuses a worker may take: something is outstanding and the provider can settle it.
    /// </summary>
    /// <remarks>
    /// Exactly the set <c>IX_PublishingRecords_Status_AvailableAt</c> is filtered on, so the claim reads an
    /// index that holds only claimable rows. Terminal records — published, cancelled, failed — and records
    /// waiting for a person are not here, which is what makes "needs attention" a resting place rather than a
    /// loop.
    /// </remarks>
    private static readonly PublishingRecordStatus[] Claimable =
    [
        PublishingRecordStatus.Pending,
        PublishingRecordStatus.Submitting,
        PublishingRecordStatus.Scheduled,
        PublishingRecordStatus.Cancelling,
    ];

    /// <summary>
    /// Takes up to <see cref="PublishingLedgerPolicy.ReconcileClaimBatchSize"/> due records and holds them
    /// under <paramref name="leaseToken"/>.
    /// </summary>
    /// <remarks>
    /// A batch rather than one, because two workers polling the same queue pick the same oldest row: the loser
    /// of a conditional update moves to the next candidate instead of starting over. The batch is also what
    /// lets a pass group records by channel and spend one provider call on all of them.
    /// </remarks>
    public async Task<IReadOnlyList<PublishingReconciliationClaim>> ClaimDueAsync(
        Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var expiresAt = (DateTimeOffset?)(now + PublishingLedgerPolicy.ReconcileLeaseDuration);

        var candidates = await context.PublishingRecords
            .IgnoreQueryFilters()
            .Where(record => Claimable.Contains(record.Status)
                && record.AvailableAt <= now
                && (record.LeasedBy == null || record.LeaseExpiresAt <= now))
            .OrderBy(record => record.AvailableAt)
            .Take(PublishingLedgerPolicy.ReconcileClaimBatchSize)
            .Select(record => new
            {
                record.Id,
                record.WorkspaceId,
                record.PublishingProfileId,
                record.Status,
            })
            .ToListAsync(cancellationToken);

        var claimed = new List<PublishingReconciliationClaim>(candidates.Count);

        foreach (var candidate in candidates)
        {
            // The guard is the claim. A row another worker took between the scan and here no longer matches
            // it, so the update affects nothing and this worker moves on to the next candidate.
            var won = await context.PublishingRecords
                .IgnoreQueryFilters()
                .Where(record => record.Id == candidate.Id
                    && record.Status == candidate.Status
                    && (record.LeasedBy == null || record.LeaseExpiresAt <= now))
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(record => record.LeasedBy, (Guid?)leaseToken)
                        .SetProperty(record => record.LeaseExpiresAt, expiresAt),
                    cancellationToken);

            if (won is 0)
            {
                continue;
            }

            claimed.Add(new PublishingReconciliationClaim(
                candidate.Id, candidate.WorkspaceId, candidate.PublishingProfileId, candidate.Status, leaseToken));
        }

        return claimed;
    }

    /// <summary>
    /// Releases claims whose worker never came back, so another may take them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It clears the lease and nothing else — no status moves and no attempt is counted here. A lapsed lease
    /// means nobody knows how far that worker got, and the record's own state already says what is in doubt:
    /// a <c>Submitting</c> row with an uncertain submit is reconciled, not resubmitted, so recovering it
    /// cannot produce a second post.
    /// </para>
    /// <para>
    /// The attempt budget is spent by the work itself, which is why a crash loop cannot exhaust it here and
    /// a poisoned record still reaches "needs attention" by the ordinary path.
    /// </para>
    /// </remarks>
    public Task<int> ReleaseLapsedAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        context.PublishingRecords
            .IgnoreQueryFilters()
            .Where(record => record.LeasedBy != null && record.LeaseExpiresAt <= now)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(record => record.LeasedBy, (Guid?)null)
                    .SetProperty(record => record.LeaseExpiresAt, (DateTimeOffset?)null),
                cancellationToken);
}
