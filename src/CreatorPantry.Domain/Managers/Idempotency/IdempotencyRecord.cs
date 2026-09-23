namespace CreatorPantry.Domain.Managers.Idempotency;

/// <summary>
/// The committed outcome of one idempotent command, scoped to a user, workspace, and operation. Stores an
/// HMAC fingerprint of the request (never the payload) and the successful result as JSON.
/// </summary>
public class IdempotencyRecord
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    /// <summary>The workspace scope; <see cref="Idempotency.IdempotencyPolicy.NoWorkspace"/> when not workspace-owned.</summary>
    public Guid WorkspaceId { get; set; }

    public string Operation { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    /// <summary>HMAC-SHA256 of the operation and its canonical fingerprint.</summary>
    public byte[] FingerprintHash { get; set; } = [];

    public string ResultJson { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}
