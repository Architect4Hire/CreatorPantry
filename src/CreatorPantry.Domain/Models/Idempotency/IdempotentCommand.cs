using CreatorPantry.Domain.Models.Results;

namespace CreatorPantry.Domain.Models.Idempotency;

/// <summary>A command a facade has declared idempotent.</summary>
/// <param name="UserId">The authenticated caller. Never taken from request input.</param>
/// <param name="WorkspaceId">The resolved workspace, or null for account-level commands.</param>
/// <param name="Operation">A stable operation name, e.g. <c>publications.confirm</c>.</param>
/// <param name="Key">The client's <c>Idempotency-Key</c> header value, if sent.</param>
/// <param name="Fingerprint">
/// What makes two requests "the same": the operation's meaningful inputs. Never include secrets; it is
/// HMAC-hashed and never stored, but it should not need to be sensitive.
/// </param>
/// <param name="KeyRequired">When false, a request without a key simply runs once, unprotected.</param>
public sealed record IdempotentCommand(
    string UserId, Guid? WorkspaceId, string Operation, string? Key, object Fingerprint, bool KeyRequired = true);

/// <summary>The result, and whether it was replayed from an earlier committed request.</summary>
public sealed record IdempotentOutcome<T>(OperationResult<T> Result, bool Replayed);
