using CreatorPantry.Domain.Managers.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Managers.Idempotency;

public interface IIdempotencyRepository
{
    Task DeleteExpiredAsync(IdempotencyScope scope, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Inserts the record; false when the scope already has one (unique index).</summary>
    Task<bool> TryInsertAsync(IdempotencyRecord record, CancellationToken cancellationToken);

    Task<StoredIdempotentResult?> FindAsync(IdempotencyScope scope, CancellationToken cancellationToken);

    /// <summary>Writes the operation's result onto the claimed record, tracked or not.</summary>
    Task SaveResultAsync(IdempotencyRecord record, string resultJson, CancellationToken cancellationToken);
}

internal sealed class IdempotencyRepository(CreatorPantryDbContext context) : IIdempotencyRepository
{
    public async Task DeleteExpiredAsync(IdempotencyScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The scope is unique, so this is a single indexed row; expiry is compared in code to keep the query
        // provider-neutral (not every provider translates DateTimeOffset comparisons).
        var record = await InScope(scope).SingleOrDefaultAsync(cancellationToken);
        if (record is not null && record.ExpiresAt <= now)
        {
            context.IdempotencyRecords.Remove(record);
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<bool> TryInsertAsync(IdempotencyRecord record, CancellationToken cancellationToken)
    {
        context.IdempotencyRecords.Add(record);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // Unique scope violation: another request committed this key first.
            context.Entry(record).State = EntityState.Detached;
            return false;
        }
    }

    public Task<StoredIdempotentResult?> FindAsync(IdempotencyScope scope, CancellationToken cancellationToken) =>
        InScope(scope).AsNoTracking()
            .Select(record => new StoredIdempotentResult(record.FingerprintHash, record.ResultJson))
            .SingleOrDefaultAsync(cancellationToken);

    public Task SaveResultAsync(IdempotencyRecord record, string resultJson, CancellationToken cancellationToken)
    {
        // An operation that lost a race may clear the change tracker and still succeed, answering with the winner's
        // result; that detaches the claimed record too. Re-attached, the result is written — otherwise the
        // placeholder would commit and every replay of the key would answer with an empty body.
        if (context.Entry(record).State == EntityState.Detached)
        {
            context.IdempotencyRecords.Attach(record);
        }

        record.ResultJson = resultJson;
        context.Entry(record).Property(row => row.ResultJson).IsModified = true;

        return context.SaveChangesAsync(cancellationToken);
    }

    private IQueryable<IdempotencyRecord> InScope(IdempotencyScope scope) =>
        context.IdempotencyRecords.Where(record =>
            record.UserId == scope.UserId
            && record.WorkspaceId == scope.WorkspaceId
            && record.Operation == scope.Operation
            && record.Key == scope.Key);
}
