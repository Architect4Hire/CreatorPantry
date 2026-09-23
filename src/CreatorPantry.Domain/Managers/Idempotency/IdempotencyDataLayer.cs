using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Managers.Idempotency;

public interface IIdempotencyDataLayer
{
    /// <summary>
    /// Runs <paramref name="operation"/> and records its successful result in one transaction on the request's
    /// DbContext, so the operation's own writes and the idempotency record commit together or not at all.
    /// </summary>
    Task<IdempotencyAttempt<T>> ExecuteAsync<T>(
        IdempotencyScope scope,
        byte[] fingerprintHash,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        Func<CancellationToken, Task<OperationResult<T>>> operation,
        Func<T, string> serializeResult,
        CancellationToken cancellationToken);
}

internal sealed class IdempotencyDataLayer(CreatorPantryDbContext context, IIdempotencyRepository records) : IIdempotencyDataLayer
{
    private const int MaxAttempts = 3;

    public Task<IdempotencyAttempt<T>> ExecuteAsync<T>(
        IdempotencyScope scope,
        byte[] fingerprintHash,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        Func<CancellationToken, Task<OperationResult<T>>> operation,
        Func<T, string> serializeResult,
        CancellationToken cancellationToken)
    {
        // A retrying execution strategy re-runs the whole unit, so each attempt starts from a clean tracker.
        return context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            for (var attempt = 1; ; attempt++)
            {
                context.ChangeTracker.Clear();
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                await records.DeleteExpiredAsync(scope, now, cancellationToken);

                var record = new IdempotencyRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = scope.UserId,
                    WorkspaceId = scope.WorkspaceId,
                    Operation = scope.Operation,
                    Key = scope.Key,
                    FingerprintHash = fingerprintHash,
                    ResultJson = "{}", // placeholder until the operation succeeds; never committed as such
                    CreatedAt = now,
                    ExpiresAt = expiresAt,
                };

                // Claims the scope. A concurrent request with the same scope blocks here until this commits.
                if (!await records.TryInsertAsync(record, cancellationToken))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    context.ChangeTracker.Clear();

                    var existing = await records.FindAsync(scope, cancellationToken);
                    if (existing is not null)
                    {
                        return IdempotencyAttempt<T>.Found(existing);
                    }

                    // The conflicting record expired and was removed between our insert and read: try again.
                    if (attempt < MaxAttempts)
                    {
                        continue;
                    }

                    throw new InvalidOperationException("The idempotency scope could not be claimed.");
                }

                var result = await operation(cancellationToken);
                if (!result.Succeeded)
                {
                    // Nothing is remembered for failures: the business writes roll back and a retry runs again.
                    await transaction.RollbackAsync(cancellationToken);
                    context.ChangeTracker.Clear();
                    return IdempotencyAttempt<T>.Ran(result);
                }

                record.ResultJson = serializeResult(result.Value!);
                await records.SaveAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return IdempotencyAttempt<T>.Ran(result);
            }
        });
    }
}
