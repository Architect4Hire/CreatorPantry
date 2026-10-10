using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Media.Data;

/// <summary>
/// What a worker learns by claiming a generation: where to look and under which lease, and nothing about
/// the request. No prompt, no proposal, no object key.
/// </summary>
public sealed record GeneratedImageClaim(Guid OperationId, Guid WorkspaceId, Guid LeaseToken, int Attempts);

/// <summary>
/// The cross-workspace reads the Media module's background work needs: claiming the next due generation,
/// recovering lapsed leases, and finding the workspaces with retention work.
/// </summary>
/// <remarks>
/// <para>
/// The fifth background queue claim, and it carries the same two conditions as the others (tenancy.md).
/// <strong>Identifiers only, and here that is literal.</strong> Every query projects to ids before it
/// materializes anything, and every write is an <c>ExecuteUpdate</c> — so no <c>GeneratedImageOperation</c>
/// entity is ever loaded by this class, and a prompt never enters this process from a workspace nobody has
/// resolved. The sibling claim repositories load the whole row and rely on their projection at the end; this
/// one does not, because a generation's row carries up to 8,000 characters of creator prompt.
/// </para>
/// <para>
/// <strong>The exemption is per file, so what lives here matters.</strong> The retention scan (12.8) is not
/// a claim and writes nothing, but it needs the same cross-workspace read, and putting it here keeps the
/// exemption list at one line rather than two. Anything added to this file inherits the carve-out silently,
/// so it has to meet the same two conditions: identifiers out, and the caller resolves the workspace before
/// reading anything else.
/// </para>
/// <para>
/// And the Worker resolves and validates the workspace through <c>IWorkspaceResolutionFacade</c> before
/// anything is read or written on the strength of an identifier it found: finding a row is not
/// authorization, and every read or write of creator data that follows is made by a facade under the
/// workspace's own query filter.
/// </para>
/// <para>
/// <strong>What these writes are.</strong> Queue state across workspaces — status, attempts, lease and
/// failure columns — and nothing else. <c>ExecuteUpdate</c> bypasses <c>WorkspaceOwnershipInterceptor</c>,
/// which is exactly why it is on the forbidden list: no <c>SetProperty</c> here names <c>WorkspaceId</c>,
/// and none ever may. The file is listed in <c>BulkOperationBoundaryTests.Exemptions</c>, which is what
/// makes granting the exception cost a reviewable line.
/// </para>
/// <para>
/// <strong>Why <c>ExecuteUpdate</c> rather than load-and-save.</strong> The sibling queues settle a race
/// with a <c>RowVersion</c> concurrency token; <c>GeneratedImageOperation</c> has none, so a load-then-save
/// claim would let two workers win the same row and each buy up to four images — the most expensive mistake
/// this product can make. A conditional update is atomic in the database and needs no token: the
/// <c>WHERE</c> is the race, and the affected-row count is who won.
/// </para>
/// </remarks>
internal sealed class GeneratedImageClaimRepository(CreatorPantryDbContext context)
{
    /// <summary>
    /// Takes the next due operation, if there is one, and holds it under <paramref name="leaseToken"/>.
    /// </summary>
    /// <remarks>
    /// A batch of candidates rather than one, because two workers polling the same queue will pick the same
    /// oldest row: the loser of the conditional update moves to the next candidate rather than sleeping and
    /// starting over.
    /// </remarks>
    public async Task<GeneratedImageClaim?> ClaimNextAsync(
        Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var expiresAt = (DateTimeOffset?)(now + MediaPolicy.GenerationLeaseDuration);

        var candidates = await context.GeneratedImageOperations
            .IgnoreQueryFilters()
            .Where(operation => operation.Status == GeneratedImageOperationStatus.Requested
                && operation.AvailableAt <= now)
            .OrderBy(operation => operation.AvailableAt)
            .Take(MediaPolicy.GenerationClaimBatchSize)
            .Select(operation => operation.Id)
            .ToListAsync(cancellationToken);

        foreach (var candidateId in candidates)
        {
            // The guard is the claim. A row another worker took between the scan and here no longer matches
            // it, so the update affects nothing and this worker moves on.
            var won = await context.GeneratedImageOperations
                .IgnoreQueryFilters()
                .Where(operation => operation.Id == candidateId
                    && operation.Status == GeneratedImageOperationStatus.Requested
                    && operation.LeasedBy == null)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(operation => operation.Status, GeneratedImageOperationStatus.Running)
                        .SetProperty(operation => operation.StatusChangedAt, now)
                        .SetProperty(operation => operation.StartedAt, operation => operation.StartedAt ?? now)
                        .SetProperty(operation => operation.Attempts, operation => operation.Attempts + 1)
                        .SetProperty(operation => operation.LeasedBy, (Guid?)leaseToken)
                        .SetProperty(operation => operation.LeaseExpiresAt, expiresAt),
                    cancellationToken);

            if (won == 0)
            {
                continue;
            }

            var claimed = await context.GeneratedImageOperations
                .IgnoreQueryFilters()
                .Where(operation => operation.Id == candidateId)
                .Select(operation => new { operation.WorkspaceId, operation.Attempts })
                .FirstOrDefaultAsync(cancellationToken);

            if (claimed is not null)
            {
                return new GeneratedImageClaim(candidateId, claimed.WorkspaceId, leaseToken, claimed.Attempts);
            }
        }

        return null;
    }

    /// <summary>
    /// Hands back operations whose lease lapsed: requeued with a backoff, or settled once attempts run out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what recovers a worker that died mid-generation. The operation goes back to
    /// <see cref="GeneratedImageOperationStatus.Requested"/> and its next attempt re-runs the variant loop —
    /// which skips every variant already staged, because the row for it exists. A crash is therefore paid
    /// for once per unfinished variant, not once per request.
    /// </para>
    /// <para>
    /// <strong>An abandoned operation is not failed outright if it staged anything.</strong> A worker that
    /// died after two of four variants left a creator two real images, and recording that as
    /// <c>Failed</c> would tell them their work was lost while it sat in storage. The count decides, which
    /// is the one read here that looks past the queue columns — still no creator content, only how many
    /// rows exist, and keyed on an operation id, so no neighbour's images are counted into it.
    /// </para>
    /// <para>
    /// Each write carries the same guard the scan used, so a sweep racing a worker's own settle — or
    /// another sweep — changes nothing that has already moved on.
    /// </para>
    /// </remarks>
    public async Task<(int Requeued, int Abandoned)> RecoverAbandonedLeasesAsync(
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lapsed = await context.GeneratedImageOperations
            .IgnoreQueryFilters()
            .Where(operation => operation.Status == GeneratedImageOperationStatus.Running
                && operation.LeaseExpiresAt != null
                && operation.LeaseExpiresAt < now)
            .OrderBy(operation => operation.LeaseExpiresAt)
            .Take(MediaPolicy.GenerationMaintenanceBatchSize)
            .Select(operation => new { operation.Id, operation.Attempts })
            .ToListAsync(cancellationToken);

        var requeued = 0;
        var abandoned = 0;

        foreach (var candidate in lapsed)
        {
            if (candidate.Attempts < MediaPolicy.GenerationMaxAttempts)
            {
                var availableAt =
                    now + MediaPolicy.GenerationBackoffFor(candidate.Attempts, Random.Shared.NextDouble());

                requeued += await Lapsed(candidate.Id, now)
                    .ExecuteUpdateAsync(
                        set => set
                            .SetProperty(operation => operation.Status, GeneratedImageOperationStatus.Requested)
                            .SetProperty(operation => operation.StatusChangedAt, now)
                            .SetProperty(operation => operation.AvailableAt, availableAt)
                            .SetProperty(operation => operation.LeasedBy, (Guid?)null)
                            .SetProperty(operation => operation.LeaseExpiresAt, (DateTimeOffset?)null),
                        cancellationToken);

                continue;
            }

            // The bound: without it a request that kills its worker every time cycles forever, and every
            // cycle is a paid provider call.
            var staged = await context.GeneratedImages
                .IgnoreQueryFilters()
                .CountAsync(image => image.GeneratedImageOperationId == candidate.Id, cancellationToken);

            var status = staged > 0
                ? GeneratedImageOperationStatus.PartiallySucceeded
                : GeneratedImageOperationStatus.Failed;
            var summary = $"Claimed {candidate.Attempts} times and never completed; no attempts remain.";

            abandoned += await Lapsed(candidate.Id, now)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(operation => operation.Status, status)
                        .SetProperty(operation => operation.StatusChangedAt, now)
                        .SetProperty(operation => operation.CompletedAt, (DateTimeOffset?)now)
                        .SetProperty(
                            operation => operation.FailureCategory,
                            GeneratedImageFailureCategory.LeaseAbandoned)
                        .SetProperty(operation => operation.FailureSummary, summary)
                        .SetProperty(operation => operation.LeasedBy, (Guid?)null)
                        .SetProperty(operation => operation.LeaseExpiresAt, (DateTimeOffset?)null),
                    cancellationToken);
        }

        return (requeued, abandoned);
    }

    /// <summary>
    /// The workspaces that currently have retention work: a staged image past its deadline, bytes to
    /// purge, or renditions whose source no longer wants them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Workspace identifiers and nothing else</strong>, which is the whole of what the sweep is
    /// told before it resolves one. It cannot see how many images a workspace has, what they are, or what
    /// anybody asked for — only that there is something to do.
    /// </para>
    /// <para>
    /// Orphan reconciliation is not in this query and cannot be: an orphan has no row, so no query over
    /// the database can know a workspace has one. The worker reconciles the workspaces this returns, which
    /// means a workspace whose only staging content is an orphan waits until it next has real work. That
    /// is a slower sweep rather than a wrong one, and it is the price of not listing a storage container
    /// once per workspace per hour forever.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Guid>> FindWorkspacesWithRetentionWorkAsync(
        DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        var expiring = context.GeneratedImages
            .IgnoreQueryFilters()
            .Where(image => image.Status == GeneratedImageStatus.Staged && image.RetentionExpiresAt <= now)
            .Select(image => image.WorkspaceId);

        var purgeable = context.GeneratedImages
            .IgnoreQueryFilters()
            .Where(image => (image.Status == GeneratedImageStatus.Rejected
                    || image.Status == GeneratedImageStatus.Expired
                    || (image.Status == GeneratedImageStatus.Kept
                        && context.MediaAssetVersions.IgnoreQueryFilters().Any(
                            version => version.SourceGeneratedImageId == image.Id)))
                && image.ObjectDeletedAt == null)
            .Select(image => image.WorkspaceId);

        // Ordered, so the page is the same page twice running rather than whatever the engine felt
        // like. It is not an anti-starvation measure: a workspace that is always skipped — unresolvable,
        // or storage persistently down for it — keeps its slot, which only bites once more than
        // RetentionBatchSize workspaces have work at the same moment. Rotating past skipped ids needs
        // state this sweep deliberately does not keep.
        // Renditions nobody wants any more: of a staged image that was settled, or of a library asset
        // that was deleted. The second is why a workspace with nothing staged can still have work here.
        var renditions = context.MediaRenditions
            .IgnoreQueryFilters()
            .Where(rendition =>
                context.GeneratedImages.IgnoreQueryFilters().Any(image => image.Id == rendition.GeneratedImageId
                    && image.Status != GeneratedImageStatus.Staged)
                || context.MediaAssets.IgnoreQueryFilters().Any(asset => asset.Id == rendition.MediaAssetId
                    && asset.DeletedAt != null))
            .Select(rendition => rendition.WorkspaceId);

        return await expiring
            .Union(purgeable)
            .Union(renditions)
            .Distinct()
            .OrderBy(workspaceId => workspaceId)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>The one operation, and only while its lease is still the lapsed one the scan saw.</summary>
    private IQueryable<Data.Entities.GeneratedImageOperation> Lapsed(Guid operationId, DateTimeOffset now) =>
        context.GeneratedImageOperations
            .IgnoreQueryFilters()
            .Where(operation => operation.Id == operationId
                && operation.Status == GeneratedImageOperationStatus.Running
                && operation.LeaseExpiresAt != null
                && operation.LeaseExpiresAt < now);
}
