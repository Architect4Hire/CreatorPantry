namespace CreatorPantry.Domain.Managers.Idempotency;

/// <summary>Idempotency limits and stable error codes (API contract: retryable commands accept a key).</summary>
public static class IdempotencyPolicy
{
    /// <summary>How long a completed key replays its result before the same key may run fresh.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public const int KeyMaxLength = 255;
    public const int OperationMaxLength = 100;

    /// <summary>The header clients send, and the header set on replayed responses.</summary>
    public const string KeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";

    public const string KeyRequiredCode = "idempotency.key_required";
    public const string KeyInvalidCode = "idempotency.key_invalid";

    /// <summary>The key was already used for a different request payload.</summary>
    public const string KeyReusedCode = "idempotency.key_reused";

    /// <summary>Workspace scope stored for operations that are not workspace-owned (e.g. account commands).</summary>
    public static readonly Guid NoWorkspace = Guid.Empty;
}

/// <summary>Server secret keying the request fingerprint HMAC (section <c>Idempotency</c>).</summary>
public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    /// <summary>Base64, at least 32 bytes. A secret: supplied by parameter or secret store only.</summary>
    public string FingerprintKey { get; set; } = string.Empty;

    public static bool IsValidKey(string value)
    {
        try
        {
            return Convert.FromBase64String(value).Length >= 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
