using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.AiUsage.Data;

/// <summary>EF access to the account usage ledger. Reads and writes no other module's tables.</summary>
internal interface IAiUsageRepository
{
    /// <inheritdoc cref="IAiUsageDataLayer.FindPostedAttemptNumbersAsync"/>
    Task<IReadOnlySet<int>> FindPostedAttemptNumbersAsync(
        Guid aiOperationId, CancellationToken cancellationToken);

    /// <summary>Adds entries to the change tracker. The caller's save commits them.</summary>
    void AddRange(IEnumerable<AccountAiUsageEntry> entries);
}

/// <inheritdoc cref="IAiUsageRepository"/>
internal sealed class AiUsageRepository(CreatorPantryDbContext context) : IAiUsageRepository
{
    /// <remarks>
    /// <para>
    /// <strong>No <c>IgnoreQueryFilters()</c>, because there is no filter to ignore.</strong>
    /// <c>AccountAiUsageEntry</c> is not <see cref="IWorkspaceOwned"/>, so this reads across every workspace by
    /// construction rather than by opting out of scoping — which is why it is not a
    /// <c>BulkOperationBoundaryTests</c> exemption and does not need to be.
    /// </para>
    /// <para>
    /// Seeks <c>UX_AccountAiUsageEntries_Operation_Attempt</c>, and projects to the attempt number alone: this
    /// question is "which of these are already posted", and reading whole rows to answer it would pull counts
    /// and costs nobody asked for.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlySet<int>> FindPostedAttemptNumbersAsync(
        Guid aiOperationId, CancellationToken cancellationToken) =>
        (await context.AccountAiUsageEntries
            .Where(entry => entry.AiOperationId == aiOperationId)
            .Select(entry => entry.AttemptNumber)
            .ToListAsync(cancellationToken))
        .ToHashSet();

    public void AddRange(IEnumerable<AccountAiUsageEntry> entries) =>
        context.AccountAiUsageEntries.AddRange(entries);
}
