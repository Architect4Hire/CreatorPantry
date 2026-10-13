namespace CreatorPantry.Domain.Modules.Publishing.Managers;

/// <summary>
/// The network a publishing profile posts to, in CreatorPantry's own vocabulary.
/// </summary>
/// <remarks>
/// Not a provider's enum: a provider's name for a network is mapped to one of these at the integration
/// boundary, and a network this list does not know is <see cref="Other"/> rather than a failure — a provider
/// adding one must not stop a workspace reading the profiles it already has. Stored as its integer, so members
/// are appended and never renumbered. Zero is not a platform.
/// </remarks>
public enum PublishingPlatform
{
    Other = 1,
    Instagram = 2,
    Facebook = 3,
    X = 4,
    LinkedIn = 5,
    Pinterest = 6,
    TikTok = 7,
    YouTube = 8,
    Mastodon = 9,
    Bluesky = 10,
    Threads = 11,
    WhatsApp = 12,
}

/// <summary>Whether a publishing profile can be posted to, as last reported by its provider.</summary>
public enum PublishingProfileStatus
{
    Ready = 1,

    /// <summary>The provider has lost its connection to the network. The creator reconnects it at the provider.</summary>
    NeedsReconnecting = 2,

    /// <summary>The provider holds the profile but will not post to it, typically a plan limit.</summary>
    Locked = 3,

    /// <summary>
    /// The provider no longer lists it. Kept, never deleted: a publishing record may point at it, and "this
    /// went to a profile that has since been removed" has to stay sayable.
    /// </summary>
    Removed = 4,
}

/// <summary>Where this workspace stands with its publishing provider, as of the last time it was asked.</summary>
public enum PublishingConnectionStatus
{
    /// <summary>The provider answered and the profiles are as it gave them.</summary>
    Connected = 1,

    /// <summary>
    /// No provider is configured for this workspace. Not an error: the workspace can still plan, export and
    /// download, and any profiles read earlier are still listed.
    /// </summary>
    NotConfigured = 2,

    /// <summary>The provider refused the configured credential. An operator has to replace it.</summary>
    CredentialRejected = 3,

    /// <summary>The provider is throttling this deployment. The profiles listed are from the last good read.</summary>
    RateLimited = 4,

    /// <summary>The provider could not be reached or did not answer usefully. The profiles listed are from the last good read.</summary>
    Unavailable = 5,
}

/// <summary>Limits and timings for publishing profiles.</summary>
public static class PublishingPolicy
{
    /// <summary>The one provider there is. A key, not a display name, and never a credential.</summary>
    public const string BufferProviderKey = "buffer";

    public const int ProviderKeyMaxLength = 32;
    public const int DisplayNameMaxLength = 200;
    public const int TimeZoneIdMaxLength = 64;
    public const int ExternalIdMaxLength = 128;

    /// <summary>
    /// How long a successful read is served from storage before the provider is asked again.
    /// </summary>
    /// <remarks>
    /// The provider's quota is small and shared by everything this deployment does with it — 250 requests a
    /// day on its free plan, and one read here costs at least two. Profiles change when a creator connects a
    /// network, which is rare; reading them on every page view would spend the day's quota by mid-morning and
    /// leave none for scheduling.
    /// </remarks>
    public static readonly TimeSpan ProfilesFreshFor = TimeSpan.FromMinutes(15);

    /// <summary>The shortest gap between two asks of the provider, even when a creator asks for a refresh.</summary>
    public static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromMinutes(1);

    /// <summary>How long to leave the provider alone after it could not be reached.</summary>
    public static readonly TimeSpan UnavailableBackoff = TimeSpan.FromMinutes(2);

    /// <summary>How long to leave it alone after it refused the credential, or throttled without saying for how long.</summary>
    public static readonly TimeSpan RefusedBackoff = TimeSpan.FromMinutes(15);

    /// <summary>The longest a provider's own "retry after" is believed for.</summary>
    public static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromHours(24);

    /// <summary>Caps on one read, so a provider's answer cannot write unbounded rows.</summary>
    public const int MaxProviderAccounts = 10;
    public const int MaxProfilesPerRead = 200;
}

/// <summary>One place this workspace can publish to.</summary>
/// <param name="Id">CreatorPantry's own id for it. The provider's id never leaves the server.</param>
/// <param name="ProviderKey">Which publishing provider holds it, as a stable key such as <c>buffer</c>.</param>
/// <param name="Platform">The network it posts to.</param>
/// <param name="DisplayName">Its name as the provider gave it.</param>
/// <param name="TimeZoneId">The IANA zone the provider schedules it in, or null when it gave none this server recognises.</param>
/// <param name="Status">Whether it can be posted to, as last reported.</param>
/// <param name="LastSeenAt">When the provider last listed it (UTC).</param>
public sealed record PublishingProfileServiceModel(
    Guid Id,
    string ProviderKey,
    PublishingPlatform Platform,
    string DisplayName,
    string? TimeZoneId,
    PublishingProfileStatus Status,
    DateTimeOffset LastSeenAt);

/// <summary>Where the workspace stands with its publishing provider.</summary>
/// <param name="ProviderKey">The provider, or null when none is configured for this workspace.</param>
/// <param name="Status">The outcome of the last time the provider was asked.</param>
/// <param name="LastCheckedAt">When it was last asked (UTC), or null if it never has been.</param>
/// <param name="LastSucceededAt">When it last answered with profiles (UTC), or null if it never has.</param>
/// <param name="RetryAfterSeconds">
/// How long until the provider will be asked again, when it is being left alone; otherwise null. CreatorPantry's
/// own number, never a provider header relayed.
/// </param>
public sealed record PublishingConnectionServiceModel(
    string? ProviderKey,
    PublishingConnectionStatus Status,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? LastSucceededAt,
    int? RetryAfterSeconds);

/// <summary>The workspace's publishing profiles, and how current they are.</summary>
/// <param name="Connection">Where the workspace stands with its provider. Always present.</param>
/// <param name="Profiles">
/// The profiles last read, by network and then name. Still listed when the provider cannot be reached or is no
/// longer configured: what a workspace already read stays readable.
/// </param>
public sealed record PublishingProfilesServiceModel(
    PublishingConnectionServiceModel Connection,
    IReadOnlyList<PublishingProfileServiceModel> Profiles);
