namespace CreatorPantry.Domain.Modules.Auth.Managers;

/// <summary>
/// The ops clients a deployment provisions, and the keys they authenticate with (baseline B-14).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Keys arrive through the secret store and nowhere else.</strong> There is no route that creates,
/// lists, returns, or rotates a key — B-14 says keys are "delivered through the secret store", and an API
/// that could mint one would be a second, weaker path to the same privilege. Rotation is changing the
/// parameter and restarting the migration service.
/// </para>
/// <para>
/// With no clients configured, the seeder does nothing and every ops route answers 401. That is the default,
/// so a clean clone has no operator credential it did not ask for.
/// </para>
/// </remarks>
public sealed class OpsApiClientOptions
{
    public const string SectionName = "Ops";

    public List<OpsApiClientDefinition> Clients { get; } = [];
}

/// <summary>One provisioned ops client. Bound from configuration, so every member is settable.</summary>
public sealed class OpsApiClientDefinition
{
    /// <summary>The automation's name. Unique, and the seeder's insert-or-rotate key.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The full <c>cpops_&lt;prefix&gt;.&lt;secret&gt;</c> credential, from the secret store.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Granted <see cref="OpsScopes"/> values. An unrecognised scope fails the seed run.</summary>
    public List<string> Scopes { get; } = [];
}
