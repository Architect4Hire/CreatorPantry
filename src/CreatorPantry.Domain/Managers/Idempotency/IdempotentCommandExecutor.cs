using CreatorPantry.Domain.Managers.Results;

namespace CreatorPantry.Domain.Managers.Idempotency;

/// <summary>
/// Facade helper for commands declared idempotent. Not applied automatically: a facade opts in per operation,
/// wrapping its single Business call. The wrapped operation must be short (long work returns 202 and a job)
/// and must write only through the request's DbContext so its writes commit with the idempotency record.
/// </summary>
public interface IIdempotentCommandExecutor
{
    Task<IdempotentOutcome<T>> ExecuteAsync<T>(
        IdempotentCommand command, Func<CancellationToken, Task<OperationResult<T>>> operation, CancellationToken cancellationToken);
}

internal sealed class IdempotentCommandExecutor(IIdempotencyBusiness business) : IIdempotentCommandExecutor
{
    public Task<IdempotentOutcome<T>> ExecuteAsync<T>(
        IdempotentCommand command, Func<CancellationToken, Task<OperationResult<T>>> operation, CancellationToken cancellationToken) =>
        business.ExecuteAsync(command, operation, cancellationToken);
}
