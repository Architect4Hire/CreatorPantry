using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Content.Data;

public interface IPromptRecordDataLayer
{
    /// <summary>
    /// Writes one prompt record. False when the insert collided and nothing was written; the layer above
    /// classifies that, because deciding what a collision meant needs the pins re-read.
    /// </summary>
    Task<bool> SaveAsync(PromptRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one page of the library, and the total when the caller asked for it.
    /// </summary>
    Task<(IReadOnlyList<PromptSummaryRecord> Rows, bool HasMore, int? Total)> SearchAsync(
        PromptSearchCriteria criteria, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one prompt of the resolved workspace in full, or null when this workspace has none with that id.
    /// </summary>
    /// <remarks>
    /// A single read, so there is nothing to compose and no transaction to own — the method exists because the
    /// seam does, and because a Business layer that reached the repository directly would be the defect
    /// backend.md names. Nothing is cached, for the reason <c>IPromptRecordFacade.SearchAsync</c> records.
    /// </remarks>
    Task<PromptRecord?> GetDetailAsync(Guid promptRecordId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPromptRecordDataLayer"/>
internal sealed class PromptRecordDataLayer(
    IPromptRecordRepository records,
    IPromptRecordSearchRepository search,
    CreatorPantryDbContext context) : IPromptRecordDataLayer
{
    public async Task<(IReadOnlyList<PromptSummaryRecord> Rows, bool HasMore, int? Total)> SearchAsync(
        PromptSearchCriteria criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var (rows, hasMore) = await search.SearchAsync(criteria, cancellationToken);

        // The page first, so a caller that asked for a total still gets their rows from the cheaper statement if
        // the count is what fails. Skipped entirely when unwanted: an unasked-for count is a query nobody reads.
        var total = criteria.IncludeTotal
            ? await search.CountAsync(criteria, cancellationToken)
            : (int?)null;

        return (rows, hasMore, total);
    }

    public Task<PromptRecord?> GetDetailAsync(Guid promptRecordId, CancellationToken cancellationToken) =>
        records.FindAsync(promptRecordId, cancellationToken);

    /// <summary>
    /// The transaction boundary for a prompt save — which is, deliberately, whatever transaction the caller
    /// already has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>No transaction is opened here, and that is the linkage to DAM-001 rather than the absence of
    /// one.</strong> This is a single insert, so EF wraps it in an implicit transaction when it runs alone, and
    /// enlists in the ambient one when the caller has already begun it on this scope's <c>DbContext</c> — which
    /// both a request carrying an <c>Idempotency-Key</c> and, later, DAM-001 will have. Opening one here would
    /// have to be conditional on <c>CurrentTransaction</c> anyway (the shape
    /// <c>WorkspaceWeeklyThemeDataLayer.ReplaceAsync</c> needs, because a week replace is two ordered statement
    /// batches); one insert needs no ordering, so the simpler thing is also the correct one.
    /// </para>
    /// <para>
    /// <strong>Which direction the linkage runs matters, and immutability decides it.</strong> DAM-001 calls
    /// this inside its own transaction, so the asset row and the prompt row commit together or not at all.
    /// Writing the prompt afterwards is not an alternative: <see cref="IImmutableRecord"/> means
    /// <c>ImmutableRecordInterceptor</c> refuses every delete, so a prompt record that has committed beside an
    /// asset whose object copy then fails cannot be compensated — there is no undo to write. A prompt row that
    /// was never committed costs nothing; one committed in error is permanent.
    /// </para>
    /// <para>
    /// <strong>A failure detaches this record rather than clearing the change tracker.</strong> Clearing it is
    /// what the weekly-theme layer does and may, because a failure there ends the whole request; here the
    /// caller may be DAM-001 mid-transaction with its own entities staged, and detaching only what this method
    /// added leaves those alone to be rolled back by whoever owns them.
    /// </para>
    /// </remarks>
    public async Task<bool> SaveAsync(PromptRecord record, CancellationToken cancellationToken)
    {
        records.Add(record);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Nothing committed. The constraints reachable from this seam are the recipe, version and proposal
            // foreign keys, all of which were resolved through their owning facade moments ago — so this is a
            // race, a deadlock victim or a timeout, and which one it was is a question for the layer that can
            // ask the pins again.
            context.Entry(record).State = EntityState.Detached;

            return false;
        }

        return true;
    }
}
