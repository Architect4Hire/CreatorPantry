using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Domain.Managers.Idempotency;

public interface IIdempotencyBusiness
{
    Task<IdempotentOutcome<T>> ExecuteAsync<T>(
        IdempotentCommand command, Func<CancellationToken, Task<OperationResult<T>>> operation, CancellationToken cancellationToken);
}

internal sealed class IdempotencyBusiness(
    IIdempotencyDataLayer dataLayer, IClock clock, IOptions<IdempotencyOptions> options) : IIdempotencyBusiness
{
    private static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web);

    public async Task<IdempotentOutcome<T>> ExecuteAsync<T>(
        IdempotentCommand command, Func<CancellationToken, Task<OperationResult<T>>> operation, CancellationToken cancellationToken)
    {
        EnsureStorable(typeof(T));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.UserId);
        if (string.IsNullOrWhiteSpace(command.Operation) || command.Operation.Length > IdempotencyPolicy.OperationMaxLength)
        {
            throw new ArgumentException("Operation names are required and at most 100 characters.", nameof(command));
        }

        if (string.IsNullOrEmpty(command.Key))
        {
            return command.KeyRequired
                ? Failure<T>(IdempotencyPolicy.KeyRequiredCode, $"This request requires an {IdempotencyPolicy.KeyHeader} header.")
                : new IdempotentOutcome<T>(await operation(cancellationToken), Replayed: false);
        }

        if (!IsValidKey(command.Key))
        {
            return Failure<T>(IdempotencyPolicy.KeyInvalidCode,
                $"{IdempotencyPolicy.KeyHeader} must be 1–{IdempotencyPolicy.KeyMaxLength} printable ASCII characters.");
        }

        var scope = new IdempotencyScope(
            command.UserId, command.WorkspaceId ?? IdempotencyPolicy.NoWorkspace, command.Operation, command.Key);
        var fingerprint = Fingerprint(command.Operation, command.Fingerprint);
        var now = clock.UtcNow;

        var attempt = await dataLayer.ExecuteAsync(scope, fingerprint, now, now + IdempotencyPolicy.Retention, operation,
            value => JsonSerializer.Serialize(value, ResultJson), cancellationToken);

        if (attempt.Executed is { } executed)
        {
            return new IdempotentOutcome<T>(executed, Replayed: false);
        }

        var existing = attempt.Existing!;
        if (!CryptographicOperations.FixedTimeEquals(existing.FingerprintHash, fingerprint))
        {
            return Failure<T>(IdempotencyPolicy.KeyReusedCode,
                "This idempotency key was already used for a different request.");
        }

        var replayed = JsonSerializer.Deserialize<T>(existing.ResultJson, ResultJson)!;
        return new IdempotentOutcome<T>(OperationResult<T>.Success(replayed), Replayed: true);
    }

    /// <summary>HMAC-SHA256 of the operation and a canonical (key-sorted) JSON form of the fingerprint.</summary>
    internal byte[] Fingerprint(string operation, object fingerprint)
    {
        var canonical = Canonicalize(JsonSerializer.SerializeToNode(fingerprint, fingerprint.GetType(), ResultJson));
        var key = Convert.FromBase64String(options.Value.FingerprintKey);
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(operation + "\n" + (canonical?.ToJsonString() ?? "null")));
    }

    private static JsonNode? Canonicalize(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(property => property.Key, StringComparer.Ordinal)
            .Select(property => KeyValuePair.Create(property.Key, Canonicalize(property.Value)))),
        JsonArray array => new JsonArray([.. array.Select(Canonicalize)]),
        _ => node?.DeepClone(),
    };

    private static bool IsValidKey(string key) =>
        key.Length <= IdempotencyPolicy.KeyMaxLength && key.All(character => character is >= '!' and <= '~');

    /// <summary>Results are stored as JSON for replay; binary payloads are never stored.</summary>
    private static void EnsureStorable(Type type)
    {
        if (type == typeof(byte[]) || typeof(Stream).IsAssignableFrom(type) || type == typeof(ReadOnlyMemory<byte>)
            || type == typeof(Memory<byte>))
        {
            throw new NotSupportedException($"Idempotent results of type {type.Name} cannot be stored; return a ServiceModel.");
        }
    }

    private static IdempotentOutcome<T> Failure<T>(string code, string message) =>
        new(OperationResult<T>.Failure(new OperationError(code, message, new Dictionary<string, string[]>())), Replayed: false);
}
