using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Ai.Data;

/// <summary>
/// One provider attempt with no ledger entry, in the only terms this boundary may carry out.
/// </summary>
/// <param name="AiOperationId">The operation it belonged to.</param>
/// <param name="AttemptNumber">Its number within that operation. With the id above, the ledger's identity.</param>
/// <param name="CompletedAt">When the attempt ended, which is the instant the ledger records it at.</param>
/// <remarks>
/// Ids, instants, counts, bounded provider identifiers and enums. There is nowhere here for a recipe title, a
/// prompt, a proposal, or a failure summary — the last deliberately, because <c>AiExecutionMetadata</c> has one
/// and it is the single free-text column on the row this reads. Carrying it out would put creator-adjacent text
/// through a query that has stepped outside the workspace filter.
/// </remarks>
internal sealed record UnpostedAiAttempt(
    Guid AiOperationId,
    int AttemptNumber,
    DateTimeOffset CompletedAt,
    string ProviderName,
    string ModelName,
    string? ModelDeployment,
    int? InputTokens,
    int? OutputTokens,
    decimal? EstimatedCost,
    bool SafetyBlocked,
    AiFailureCategory? FailureCategory);

/// <summary>Who an operation's attempts are charged to, resolved across the workspace boundary.</summary>
/// <param name="AiOperationId">The operation asked about.</param>
/// <param name="WorkspaceId">Where the work was done — a reporting dimension on the ledger, never an owner.</param>
/// <param name="TaskType">Which capability ran.</param>
/// <param name="AccountId">
/// The Identity account behind the requesting membership, or null when it can no longer be resolved.
/// </param>
internal sealed record AiAttemptAttribution(
    Guid AiOperationId, Guid WorkspaceId, AiTaskType TaskType, string? AccountId);

/// <summary>
/// Finds provider attempts the account ledger never received, and says who they belong to.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This file is the second documented carve-out from tenancy.md's <c>IgnoreQueryFilters</c>
/// prohibition</strong>, and like <see cref="AiOperationClaimRepository"/> it is deliberately the smallest file
/// that could be. <c>BulkOperationBoundaryTests</c> exempts it by path, so widening the exception costs a line
/// in a diff a reviewer sees.
/// </para>
/// <para>
/// <strong>The carve-out is unavoidable rather than convenient.</strong> "Which attempts did nobody post?" is
/// not a question any single workspace can answer — the ledger it is compared against is platform-scoped by
/// design (USAGE-002), and a pass that ran per workspace would have to enumerate every workspace to answer a
/// question about none of them. There is no <c>IWorkspaceContext</c> here, and the global filter throws rather
/// than quietly returning nothing when none is resolved.
/// </para>
/// <para>
/// <strong>The constraint that keeps it safe: what crosses the boundary is identifiers, counts and enums.</strong>
/// <see cref="UnpostedAiAttempt"/> and <see cref="AiAttemptAttribution"/> have nowhere to put a title, a
/// snapshot, a prompt or a failure summary. Everything read here is written to the account ledger, which holds
/// counts and never content for exactly the same reason — so nothing this touches widens what the unfiltered
/// table already contains.
/// </para>
/// <para>
/// <strong>It writes nothing here.</strong> Posting goes through the AiUsage module's own facade, which
/// deduplicates by attempt; this type only reads. In particular it never touches a quota period: backfilled
/// usage is history, not spend, and a creator's attempts from before the ledger existed must not retroactively
/// eat the allowance they have now.
/// </para>
/// </remarks>
internal sealed class AiUsageReconciliationRepository(CreatorPantryDbContext context)
{
    /// <summary>
    /// Attempts completed in <c>[from, to)</c> that the ledger has no entry for, oldest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An anti-join: the scan side seeks <c>IX_AiExecutionMetadata_CompletedAt</c>, which deliberately does not
    /// lead with the workspace for the reason the queue claim's index does not — a sweep is looking for work
    /// before it knows whose it is. The probe side seeks
    /// <c>UX_AccountAiUsageEntries_Operation_Attempt</c>, which is the ledger's own identity.
    /// </para>
    /// <para>
    /// <strong>The query is its own bookmark.</strong> What still needs posting is defined by the data rather
    /// than by a stored cursor, so a restart, a redeploy and a duplicate delivery are the same case: the next
    /// pass finds exactly what is left. That is why this seam has no state table.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<UnpostedAiAttempt>> FindUnpostedAsync(
        DateTimeOffset from, DateTimeOffset to, int take, CancellationToken cancellationToken) =>
        await context.AiExecutionMetadata
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(attempt => attempt.CompletedAt >= from && attempt.CompletedAt < to)
            .Where(attempt => !context.AccountAiUsageEntries.Any(entry =>
                entry.AiOperationId == attempt.AiOperationId
                && entry.AttemptNumber == attempt.AttemptNumber))
            .OrderBy(attempt => attempt.CompletedAt)
            .Take(take)
            .Select(attempt => new UnpostedAiAttempt(
                attempt.AiOperationId,
                attempt.AttemptNumber,
                attempt.CompletedAt,
                attempt.ProviderName,
                attempt.ModelName,
                attempt.ModelDeployment,
                attempt.InputTokens,
                attempt.OutputTokens,
                attempt.EstimatedCost,
                attempt.SafetyBlocked,
                attempt.FailureCategory))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// How many attempts in <c>[from, to)</c> the ledger has no entry for. The drift figure, without reading
    /// the rows.
    /// </summary>
    public async Task<int> CountUnpostedAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        await context.AiExecutionMetadata
            .IgnoreQueryFilters()
            .Where(attempt => attempt.CompletedAt >= from && attempt.CompletedAt < to)
            .Where(attempt => !context.AccountAiUsageEntries.Any(entry =>
                entry.AiOperationId == attempt.AiOperationId
                && entry.AttemptNumber == attempt.AttemptNumber))
            .CountAsync(cancellationToken);

    /// <summary>
    /// Who each of these operations' attempts is charged to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A left join through the requesting membership to the Identity account, because usage is measured per
    /// account and membership is the workspace-scoped identity — one person is a different membership in every
    /// workspace and the same account in all of them (USAGE-001).
    /// </para>
    /// <para>
    /// <strong>Memberships are read whatever their status.</strong> Removal is a status here rather than a
    /// delete, and a departed member's past attempts still belong to their account — narrowing this to active
    /// memberships would silently drop exactly the history this seam exists to recover.
    /// </para>
    /// <para>
    /// <strong>The membership is matched on its workspace as well as its id, and that pairing is the whole
    /// safety of this join.</strong> The filtered paths that answer the same question check it twice — the
    /// tenancy repository pairs both halves, and the live recording path throws outright rather than post an
    /// entry whose membership is not the operation's. Verifying it nowhere on the one path that runs
    /// <em>outside</em> the filter would be backwards. The failure it prevents is a permanent, immutable
    /// ledger row carrying one workspace's account against another's id, which the account read seam would
    /// then show to somebody who was never a member there — and nothing downstream could detect or undo it.
    /// </para>
    /// <para>
    /// <strong><c>IgnoreQueryFilters</c> on the membership subquery is belt-and-braces rather than the thing
    /// doing the work.</strong> Membership's own filter is permissive rather than strict, so with no resolved
    /// context it reads every workspace regardless — which is exactly why <c>WorkspaceMembership</c>'s remarks
    /// insist that any query against it supply its own explicit predicate. The predicate above is that.
    /// </para>
    /// <para>
    /// A null account is returned rather than the row being dropped, so the caller can count what it could not
    /// attribute instead of losing it quietly. Unreachable today, because nothing deletes a membership; handled
    /// anyway, on the reasoning <c>AccountAiUsageEntry</c> gives for declaring no foreign keys at all.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<AiAttemptAttribution>> FindAttributionsAsync(
        IReadOnlyCollection<Guid> operationIds, CancellationToken cancellationToken) =>
        await context.AiOperations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(operation => operationIds.Contains(operation.Id))
            .Select(operation => new AiAttemptAttribution(
                operation.Id,
                operation.WorkspaceId,
                operation.TaskType,
                context.WorkspaceMemberships
                    .IgnoreQueryFilters()
                    .Where(membership => membership.Id == operation.RequestedByMembershipId
                        && membership.WorkspaceId == operation.WorkspaceId)
                    .Select(membership => membership.UserId)
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken);
}
