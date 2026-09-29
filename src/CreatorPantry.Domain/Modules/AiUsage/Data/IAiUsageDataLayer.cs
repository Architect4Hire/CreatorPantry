using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;

namespace CreatorPantry.Domain.Modules.AiUsage.Data;

/// <summary>
/// Composes the ledger's persistence operations — and deliberately owns no transaction boundary.
/// </summary>
/// <remarks>
/// backend.md says a DataLayer owns the transaction for a multi-write operation. This one does not, and the
/// reason is on <see cref="Facade.IAiUsageRecordingFacade.StageAttemptsAsync"/>: a ledger entry has to commit
/// with the attempt record it describes, so it joins the caller's unit of work rather than opening a second
/// one. Everything else about the seam is ordinary — nothing above this type touches EF, and nothing below it
/// makes a product decision.
/// </remarks>
internal interface IAiUsageDataLayer
{
    /// <summary>
    /// The attempt numbers this operation has already posted, so a duplicate delivery can be skipped rather
    /// than colliding with the unique index inside the caller's transaction.
    /// </summary>
    Task<IReadOnlySet<int>> FindPostedAttemptNumbersAsync(
        Guid aiOperationId, CancellationToken cancellationToken);

    /// <summary>Stages entries on the current unit of work. Does not save.</summary>
    void StageEntries(IEnumerable<AccountAiUsageEntry> entries);
}

/// <inheritdoc cref="IAiUsageDataLayer"/>
internal sealed class AiUsageDataLayer(IAiUsageRepository entries) : IAiUsageDataLayer
{
    public Task<IReadOnlySet<int>> FindPostedAttemptNumbersAsync(
        Guid aiOperationId, CancellationToken cancellationToken) =>
        entries.FindPostedAttemptNumbersAsync(aiOperationId, cancellationToken);

    public void StageEntries(IEnumerable<AccountAiUsageEntry> staged) => entries.AddRange(staged);
}
