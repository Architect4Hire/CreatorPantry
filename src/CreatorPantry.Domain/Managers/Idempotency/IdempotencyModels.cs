using CreatorPantry.Domain.Managers.Results;

namespace CreatorPantry.Domain.Managers.Idempotency;

/// <summary>Where a key is unique: never a bare key.</summary>
public sealed record IdempotencyScope(string UserId, Guid WorkspaceId, string Operation, string Key);

/// <summary>A previously committed outcome for the same scope.</summary>
public sealed record StoredIdempotentResult(byte[] FingerprintHash, string ResultJson);

/// <summary>Either the operation ran in this attempt, or a committed record already existed for the scope.</summary>
public sealed record IdempotencyAttempt<T>(OperationResult<T>? Executed, StoredIdempotentResult? Existing)
{
    public static IdempotencyAttempt<T> Ran(OperationResult<T> result) => new(result, null);

    public static IdempotencyAttempt<T> Found(StoredIdempotentResult existing) => new(null, existing);
}
