namespace CreatorPantry.Domain.Modules.Auth.Managers;

/// <summary>
/// What an ops API key is permitted to do (baseline B-14). A key never acts as a creator and never grants
/// workspace access, so these are platform capabilities, not workspace roles.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Scopes rather than one "is an ops key" flag.</strong> An automation that only reads usage should
/// not be able to suspend an account because it happens to hold a valid key; naming the capability is what
/// lets the two be issued separately.
/// </para>
/// <para>
/// Append-only, like the audit action codes: a scope that has been issued to a client cannot be renamed
/// without invalidating that client's grant.
/// </para>
/// </remarks>
public static class OpsScopes
{
    /// <summary>Read any account's AI usage, set or clear its quota, and suspend or restore its AI access.</summary>
    public const string AiUsageAdmin = "ai-usage.admin";

    /// <summary>Every scope the platform recognises. A configured scope outside this set is rejected at seed time.</summary>
    public static IReadOnlyList<string> All { get; } = [AiUsageAdmin];
}

/// <summary>Claim types carried by an ops API key principal.</summary>
public static class OpsClaims
{
    /// <summary>One claim per granted <see cref="OpsScopes"/> value.</summary>
    public const string Scope = "ops_scope";

    /// <summary>The ops client's display name, for the audit trail.</summary>
    public const string ClientName = "ops_client_name";
}
