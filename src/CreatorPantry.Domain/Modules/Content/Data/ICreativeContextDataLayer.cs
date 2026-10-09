using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Content.Data;

/// <summary>What became of a write to a creative context.</summary>
public enum CreativeContextWrite
{
    Saved = 0,

    /// <summary>The row moved between the read and the write. Nothing was written.</summary>
    Stale = 1,

    /// <summary>
    /// The database refused the row — in practice a foreign key or uniqueness rule that the checks made a
    /// moment earlier passed. Nothing was written.
    /// </summary>
    Refused = 2,
}

/// <summary>Composes the creative context's persistence operations and owns their transaction boundary.</summary>
public interface ICreativeContextDataLayer
{
    /// <summary>
    /// Inserts a new context with its channels and references, and its audit entry, in one save.
    /// </summary>
    /// <returns>False when the database refused the insert; nothing was written.</returns>
    Task<bool> CreateAsync(CreativeContext context, AuditEntry audit, CancellationToken cancellationToken);

    /// <inheritdoc cref="ICreativeContextRepository.FindAsync"/>
    Task<CreativeContext?> FindAsync(Guid contextId, CancellationToken cancellationToken);

    /// <inheritdoc cref="ICreativeContextRepository.FindForUpdateAsync"/>
    Task<CreativeContext?> FindForUpdateAsync(Guid contextId, CancellationToken cancellationToken);

    /// <summary>
    /// Saves the changes Business made to a context it loaded for update, with an audit entry when the change
    /// is one worth recording.
    /// </summary>
    /// <remarks>
    /// One save, one transaction: the root's <c>UPDATE</c> quotes the row version the aggregate was read with,
    /// so a change to a channel or a reference is refused along with it when another writer got there first.
    /// That only holds if the root is modified, which is why Business always moves <c>UpdatedAt</c>.
    /// </remarks>
    Task<CreativeContextWrite> SaveAsync(
        CreativeContext context, AuditEntry? audit, CancellationToken cancellationToken);

    /// <inheritdoc cref="ICreativeContextRepository.ListRecentAsync"/>
    Task<(IReadOnlyList<CreativeContextSummaryRecord> Rows, bool HasMore)> ListRecentAsync(
        CreativeContextListCriteria criteria, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ICreativeContextDataLayer"/>
internal sealed class CreativeContextDataLayer(
    ICreativeContextRepository contexts,
    IAuditWriter auditWriter,
    CreatorPantryDbContext db) : ICreativeContextDataLayer
{
    public async Task<bool> CreateAsync(CreativeContext context, AuditEntry audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(audit);

        contexts.Add(context);
        auditWriter.Record(audit);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Nothing committed, and the audit entry went with it. Cleared rather than detached one by one:
            // the context, its children and the audit row are all staged, and any left behind would ride
            // along on this scope's next save.
            db.ChangeTracker.Clear();

            return false;
        }

        return true;
    }

    public Task<CreativeContext?> FindAsync(Guid contextId, CancellationToken cancellationToken) =>
        contexts.FindAsync(contextId, cancellationToken);

    public Task<CreativeContext?> FindForUpdateAsync(Guid contextId, CancellationToken cancellationToken) =>
        contexts.FindForUpdateAsync(contextId, cancellationToken);

    public async Task<CreativeContextWrite> SaveAsync(
        CreativeContext context, AuditEntry? audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (audit is not null)
        {
            auditWriter.Record(audit);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);

            return CreativeContextWrite.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();

            return CreativeContextWrite.Stale;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();

            return CreativeContextWrite.Refused;
        }
    }

    public Task<(IReadOnlyList<CreativeContextSummaryRecord> Rows, bool HasMore)> ListRecentAsync(
        CreativeContextListCriteria criteria, CancellationToken cancellationToken) =>
        contexts.ListRecentAsync(criteria, cancellationToken);
}
