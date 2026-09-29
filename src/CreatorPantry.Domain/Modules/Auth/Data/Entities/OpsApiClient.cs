namespace CreatorPantry.Domain.Modules.Auth.Data.Entities;

/// <summary>
/// One machine client permitted to call <c>/api/v1/ops/*</c> with an API key (baseline B-14).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The key itself is never stored.</strong> Only <see cref="KeyHash"/> and <see cref="KeySalt"/>
/// are, and no route returns, lists, or logs either. Plaintext lives in the secret store and is delivered to
/// the operator from there; if it is lost, it is rotated rather than recovered.
/// </para>
/// <para>
/// <strong>Platform-scoped and deliberately not workspace-owned.</strong> An ops client never acts as a
/// creator and never grants workspace access, so there is no workspace to own it.
/// </para>
/// <para>
/// <strong>Not an <c>IImmutableRecord</c>.</strong> Rotation rewrites the hash in place and every request
/// stamps <see cref="LastUsedAt"/>; a write-once marker would refuse both. What must not be edited is the
/// history of what the key <em>did</em>, and that lives in <c>PlatformAuditLog</c>.
/// </para>
/// </remarks>
public class OpsApiClient
{
    public Guid Id { get; set; }

    /// <summary>A human name for the automation holding this key, recorded on every audit row it writes.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The key's public, non-secret first segment. Indexed and unique, so verification is a single seek
    /// rather than a scan of every client's hash.
    /// </summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>SHA-256 of <see cref="KeySalt"/> concatenated with the key's secret segment.</summary>
    /// <remarks>
    /// SHA-256 rather than a password hash on purpose: the secret is 32 random bytes, not a memorised
    /// password, so there is no dictionary to slow down — and this runs on every ops request. The salt is
    /// still per-client, so two clients issued the same key would not share a hash.
    /// </remarks>
    public byte[] KeyHash { get; set; } = [];

    /// <inheritdoc cref="KeyHash"/>
    public byte[] KeySalt { get; set; } = [];

    /// <summary>The granted <c>OpsScopes</c>, comma-delimited.</summary>
    public string Scopes { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the key was last replaced. Rotation invalidates the previous key immediately.</summary>
    public DateTimeOffset? RotatedAt { get; set; }

    /// <summary>When the client was switched off. A revoked client authenticates nothing, key or not.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>When a request last authenticated with this key (B-14: keys are audited on use).</summary>
    public DateTimeOffset? LastUsedAt { get; set; }
}
