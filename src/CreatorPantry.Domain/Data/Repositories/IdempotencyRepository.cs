using CreatorPantry.Domain.Models.DomainModels.Idempotency;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Data.Repositories;

public interface IIdempotencyRepository
{
    Task DeleteExpiredAsync(IdempotencyScope scope, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Inserts the record; false when the scope already has one (unique index).</summary>
    Task<bool> TryInsertAsync(IdempotencyRecord record, CancellationToken cancellationToken);

    Task<StoredIdempotentResult?> FindAsync(IdempotencyScope scope, CancellationToken cancellationToken);

    Task SaveAsync(CancellationToken cancellationToken);
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

    public Task SaveAsync(CancellationToken cancellationToken) => context.SaveChangesAsync(cancellationToken);

    private IQueryable<IdempotencyRecord> InScope(IdempotencyScope scope) =>
        context.IdempotencyRecords.Where(record =>
            record.UserId == scope.UserId
            && record.WorkspaceId == scope.WorkspaceId
            && record.Operation == scope.Operation
            && record.Key == scope.Key);
}
