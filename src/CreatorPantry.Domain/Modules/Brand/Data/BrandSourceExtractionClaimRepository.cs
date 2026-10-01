using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Brand.Data;

/// <summary>
/// What a claim pass learned about one queued extraction: enough to resolve a workspace and name the version,
/// nothing more.
/// </summary>
/// <remarks>
/// <strong>Identifiers only, and there is nowhere here to put anything else.</strong> No title, no filename, no
/// media type, no object key, no byte of the document. That is the condition tenancy.md attaches to the
/// queue-claim carve-out, and a record with no field for creator content cannot carry any across the boundary
/// by accident later.
/// </remarks>
/// <param name="WorkspaceId">
/// The workspace the worker must resolve and validate before it touches anything else. Finding this row is not
/// authorization to read what it names.
/// </param>
/// <param name="LeaseToken">The token the worker quotes on every later write for this claim.</param>
/// <param name="Attempts">How many times this operation has now been claimed, including this one.</param>
public sealed record BrandSourceExtractionClaim(
    Guid OperationId,
    Guid WorkspaceId,
    Guid DocumentId,
    Guid VersionId,
    Guid LeaseToken,
    int Attempts);

/// <summary>
/// The extraction queue claim, and the second place in the domain that reads across workspaces.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This file is a documented carve-out from tenancy.md's <c>IgnoreQueryFilters</c> prohibition, and it
/// is deliberately the smallest file that could be.</strong> <c>BulkOperationBoundaryTests</c> exempts it by
/// path, so widening the exception costs a line in a diff a reviewer sees.
/// </para>
/// <para>
/// The carve-out is unavoidable rather than convenient, for the reason
/// <see cref="CreatorPantry.Domain.Modules.Ai.Data.AiOperationClaimRepository"/> gives at length: a worker looks
/// for work <em>before</em> it knows which workspace it will serve, so there is no <c>IWorkspaceContext</c> to
/// filter by — and the global filter throws rather than quietly returning nothing when none is resolved. Polling
/// each workspace in turn was the alternative, and it scales with workspace count rather than queue depth.
/// </para>
/// <para>
/// The constraint that keeps it safe is that a claim returns <see cref="BrandSourceExtractionClaim"/> and
/// nothing else. The worker takes the workspace id, resolves and validates it through the ordinary tenancy path,
/// and every read afterwards goes through the filtered context like any other request — including the read of
/// this very operation row, which is workspace-owned and unreachable without one.
/// </para>
/// </remarks>
internal sealed class BrandSourceExtractionClaimRepository(CreatorPantryDbContext context)
{
    /// <summary>
    /// Claims one due extraction for <paramref name="leaseToken"/>, or returns null when the queue is empty.
    /// </summary>
    /// <remarks>
    /// Optimistic, not pessimistic. Two workers reaching the same row both write it and the row version
    /// decides — the loser gets <see cref="DbUpdateConcurrencyException"/> and moves to the next candidate
    /// rather than blocking on a lock, which is why a batch is read rather than a single row.
    /// </remarks>
    public async Task<BrandSourceExtractionClaim?> ClaimNextAsync(
        Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var candidates = await context.BrandSourceExtractionOperations
            .IgnoreQueryFilters()
            .Where(operation => operation.Status == BrandSourceExtractionOperationStatus.Queued
                && operation.AvailableAt <= now)
            .OrderBy(operation => operation.AvailableAt)
            .Take(BrandPolicy.ExtractionClaimBatchSize)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            candidate.Status = BrandSourceExtractionOperationStatus.Running;
            candidate.StatusChangedAt = now;

            // Set once and never cleared. A requeued operation has genuinely started before, and clearing this
            // would erase the evidence that a worker ever picked it up.
            candidate.StartedAt ??= now;

            candidate.Attempts++;
            candidate.LeasedBy = leaseToken;
            candidate.LeaseExpiresAt = now + BrandPolicy.ExtractionLeaseDuration;

            try
            {
                await context.SaveChangesAsync(cancellationToken);

                return new BrandSourceExtractionClaim(
                    candidate.Id,
                    candidate.WorkspaceId,
                    candidate.BrandSourceDocumentId,
                    candidate.BrandSourceDocumentVersionId,
                    leaseToken,
                    candidate.Attempts);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another worker won this row. Detach it so the next iteration is not retried against a stale
                // tracked entity, and try the next candidate.
                context.Entry(candidate).State = EntityState.Detached;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns extractions whose lease has lapsed to the queue, or fails them once the attempt bound is spent.
    /// </summary>
    /// <returns>How many were requeued, and how many were abandoned for good.</returns>
    /// <remarks>
    /// Also cross-workspace, and for the same reason: a worker died holding a claim in a workspace nobody has
    /// resolved. It carries nothing out — the counts are the whole result.
    /// </remarks>
    public async Task<(int Requeued, int Abandoned)> RecoverAbandonedLeasesAsync(
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lapsed = await context.BrandSourceExtractionOperations
            .IgnoreQueryFilters()
            .Where(operation => operation.Status == BrandSourceExtractionOperationStatus.Running
                && operation.LeaseExpiresAt != null
                && operation.LeaseExpiresAt < now)
            .Take(BrandPolicy.ExtractionClaimBatchSize)
            .ToListAsync(cancellationToken);

        var requeued = 0;
        var abandoned = 0;

        foreach (var operation in lapsed)
        {
            operation.LeasedBy = null;
            operation.LeaseExpiresAt = null;
            operation.StatusChangedAt = now;

            if (operation.Attempts >= BrandPolicy.ExtractionMaxAttempts)
            {
                // The bound. Without it this branch requeues forever, and a document that kills its worker
                // every time would cycle between Running and Queued for the life of the deployment.
                operation.Status = BrandSourceExtractionOperationStatus.Failed;
                operation.FailureCategory = BrandSourceExtractionFailureCategory.LeaseAbandoned;
                operation.FailureSummary =
                    $"Claimed {operation.Attempts} times and never completed; no attempts remain.";
                operation.CompletedAt = now;
                abandoned++;
            }
            else
            {
                operation.Status = BrandSourceExtractionOperationStatus.Queued;
                operation.AvailableAt = now + BrandPolicy.ExtractionBackoffFor(operation.Attempts);
                requeued++;
            }
        }

        if (lapsed.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        return (requeued, abandoned);
    }
}
