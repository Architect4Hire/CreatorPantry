using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Ai.Data;

/// <summary>What a claim pass learned about one queued operation: enough to resolve a workspace, nothing more.</summary>
/// <param name="OperationId">The operation to run.</param>
/// <param name="WorkspaceId">
/// The workspace the worker must resolve and validate before it touches anything else. This is the only piece
/// of workspace-owned data the claim is allowed to carry out.
/// </param>
/// <param name="LeaseToken">The token the worker presents on every later write for this claim.</param>
/// <param name="RequestedByMembershipId">
/// The membership that requested this — an identifier, like <see cref="WorkspaceId"/>, and the same class of
/// exception: the worker needs it to resolve an <see cref="CreatorPantry.Domain.Managers.Persistence.IWorkspaceContext"/>
/// before it can even read the operation row itself (workspace-owned, so strictly filtered), and there is no
/// title or snapshot for it to carry alongside.
/// </param>
/// <param name="Attempts">How many times this operation has now been claimed, including this one.</param>
public sealed record AiOperationClaim(
    Guid OperationId, Guid WorkspaceId, Guid LeaseToken, Guid RequestedByMembershipId, int Attempts);

/// <summary>
/// One proposal a landing step may still have to run, as identifiers alone.
/// </summary>
/// <param name="OperationId">The operation whose stored proposal is to be landed.</param>
/// <param name="WorkspaceId">
/// The workspace the caller must resolve and validate before it reads anything else, exactly as on
/// <see cref="AiOperationClaim"/>.
/// </param>
/// <param name="RequestedByMembershipId">The membership the workspace context is resolved for.</param>
public sealed record AiProposalLandingCandidate(
    Guid OperationId, Guid WorkspaceId, Guid RequestedByMembershipId);

/// <summary>
/// The queue claim, and the one place in the domain that reads across workspaces.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This file is the documented carve-out from tenancy.md's <c>IgnoreQueryFilters</c> prohibition, and
/// it is deliberately the smallest file that could be.</strong> <c>BulkOperationBoundaryTests</c> exempts it
/// by path, so widening the exception means editing that list in a diff a reviewer sees.
/// </para>
/// <para>
/// The carve-out is unavoidable rather than convenient. A worker looks for work <em>before</em> it knows which
/// workspace it will serve, so there is no <c>IWorkspaceContext</c> to filter by — and the global filter throws
/// rather than quietly returning nothing when none is resolved. Polling each workspace in turn was the
/// alternative and scales with workspace count rather than queue depth.
/// </para>
/// <para>
/// <strong>The constraint that keeps it safe: a claim returns identifiers only.</strong> No title, no snapshot,
/// no creator content of any kind crosses this boundary — <see cref="AiOperationClaim"/> has nowhere to put
/// any. The worker takes the workspace id, resolves and validates it through the ordinary tenancy path, and
/// everything it reads afterwards goes through the filtered context like any other request. Finding a row is
/// not authorization.
/// </para>
/// </remarks>
internal sealed class AiOperationClaimRepository(CreatorPantryDbContext context)
{
    /// <summary>
    /// Claims one due operation for <paramref name="leaseToken"/>, or returns null when the queue is empty.
    /// </summary>
    /// <remarks>
    /// Optimistic, not pessimistic. Two workers reaching the same row both write it and the row version
    /// decides — the loser gets <see cref="DbUpdateConcurrencyException"/> and moves to the next candidate
    /// rather than blocking on a lock. That keeps a slow claim from holding up every other worker, and it is
    /// why a batch is read rather than a single row.
    /// </remarks>
    public async Task<AiOperationClaim?> ClaimNextAsync(
        Guid leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var candidates = await context.AiOperations
            .IgnoreQueryFilters()
            .Where(operation => operation.Status == AiOperationStatus.Requested
                && operation.AvailableAt <= now)
            .OrderBy(operation => operation.AvailableAt)
            .Take(AiPolicy.ClaimBatchSize)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            candidate.Status = AiOperationStatus.Running;
            candidate.StatusChangedAt = now;

            // Set once and never cleared. A requeued operation has genuinely started before, and clearing this
            // to satisfy a tidier constraint would erase the evidence that a worker ever picked it up.
            candidate.StartedAt ??= now;

            candidate.Attempts++;
            candidate.LeasedBy = leaseToken;
            candidate.LeaseExpiresAt = now + AiPolicy.LeaseDuration;

            try
            {
                await context.SaveChangesAsync(cancellationToken);

                return new AiOperationClaim(
                    candidate.Id, candidate.WorkspaceId, leaseToken, candidate.RequestedByMembershipId, candidate.Attempts);
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
    /// Returns operations whose lease has lapsed to the queue, or fails them once the attempt bound is spent.
    /// </summary>
    /// <returns>How many were requeued, and how many were abandoned for good.</returns>
    /// <remarks>
    /// Also cross-workspace, and for the same reason: a worker died holding a claim in a workspace nobody has
    /// resolved. It carries nothing out — the counts are the whole result.
    /// </remarks>
    public async Task<(int Requeued, int Abandoned)> RecoverAbandonedLeasesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var lapsed = await context.AiOperations
            .IgnoreQueryFilters()
            .Where(operation => operation.Status == AiOperationStatus.Running
                && operation.LeaseExpiresAt != null
                && operation.LeaseExpiresAt < now)
            .Take(AiPolicy.ClaimBatchSize)
            .ToListAsync(cancellationToken);

        var requeued = 0;
        var abandoned = 0;

        foreach (var operation in lapsed)
        {
            operation.LeasedBy = null;
            operation.LeaseExpiresAt = null;
            operation.StatusChangedAt = now;

            if (operation.Attempts >= AiPolicy.MaxAttempts)
            {
                // The bound. Without it this branch would requeue forever and a task that kills its worker
                // every time would spend a provider budget on each pass.
                operation.Status = AiOperationStatus.Failed;
                operation.FailureCategory = AiFailureCategory.LeaseAbandoned;
                operation.CompletedAt = now;
                abandoned++;
            }
            else
            {
                operation.Status = AiOperationStatus.Requested;
                operation.AvailableAt = now + AiPolicy.RequeueDelayFor(operation.Attempts);
                requeued++;
            }
        }

        if (lapsed.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        return (requeued, abandoned);
    }

    /// <summary>
    /// Proposals of a landing task stored recently enough that their landing may still need re-running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cross-workspace, and carrying identifiers only, like every other read in this file: the caller resolves
    /// and validates each workspace through the ordinary tenancy path and asks the receiving module whether the
    /// proposal has in fact landed. Finding a row is not authorization, and a row found here says nothing
    /// about its content — <see cref="AiProposalLandingCandidate"/> has nowhere to put any.
    /// </para>
    /// <para>
    /// <strong>Bounded by a window on purpose.</strong> There is no "landed" flag to filter on — whether a
    /// proposal landed is the receiving module's fact, not this row's — so this returns every recently
    /// proposed operation of the task and the landing itself is a no-op for the ones already done. A window
    /// keeps that from being a standing cost: a proposal nobody landed within it stays readable as a proposal,
    /// and the creator's remedy is to regenerate.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<AiProposalLandingCandidate>> FindProposalsToLandAsync(
        IReadOnlyList<AiTaskType> taskTypes,
        DateTimeOffset since,
        CancellationToken cancellationToken) =>
        taskTypes.Count == 0
            ? []
            : await context.AiOperations
                .IgnoreQueryFilters()
                .Where(operation => operation.Status == AiOperationStatus.Proposed
                    && taskTypes.Contains(operation.TaskType)
                    && operation.StatusChangedAt >= since)
                // Newest first, and that is the opposite of a queue on purpose. Every row in the window is
                // returned whether it landed or not, so oldest-first would let a busy window fill the batch
                // with proposals that are already landed and never reach the one that is not — which is the
                // only row this sweep exists for.
                .OrderByDescending(operation => operation.StatusChangedAt)
                .Take(AiPolicy.ClaimBatchSize)
                .Select(operation => new AiProposalLandingCandidate(
                    operation.Id, operation.WorkspaceId, operation.RequestedByMembershipId))
                .ToListAsync(cancellationToken);

    /// <summary>
    /// Expires requests nobody claimed in time and proposals nobody acted on in time.
    /// </summary>
    /// <remarks>
    /// Deadlines are computed from <c>StatusChangedAt</c> and policy rather than stored per row, so changing a
    /// time-to-live applies to operations already in flight.
    /// </remarks>
    public async Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var requestCutoff = now - AiPolicy.RequestTimeToLive;
        var proposalCutoff = now - AiPolicy.ProposalTimeToLive;

        var due = await context.AiOperations
            .IgnoreQueryFilters()
            .Where(operation =>
                (operation.Status == AiOperationStatus.Requested && operation.RequestedAt < requestCutoff)
                || (operation.Status == AiOperationStatus.Proposed && operation.StatusChangedAt < proposalCutoff))
            .Take(AiPolicy.ClaimBatchSize)
            .ToListAsync(cancellationToken);

        foreach (var operation in due)
        {
            operation.Status = AiOperationStatus.Expired;
            operation.StatusChangedAt = now;
            operation.CompletedAt = now;
        }

        if (due.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        return due.Count;
    }
}
