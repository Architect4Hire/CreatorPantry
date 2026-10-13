using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Publishing.Managers;

namespace CreatorPantry.Domain.Modules.Publishing.Data.Entities;

/// <summary>
/// One place a workspace can publish to, as CreatorPantry knows it (PUB-001). Workspace-owned.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Provider-neutral, and it holds no provider id.</strong> What a provider calls this, and under which
/// account, is in <see cref="ExternalPublishingProfileReference"/> and nowhere else (publishing.md,
/// external.md). Everything above the data layer names a profile by <see cref="Id"/>, which CreatorPantry made.
/// </para>
/// <para>
/// <strong>A cache of what the provider said, never the creator's own words.</strong> Every field here is
/// rewritten by the next successful read, so nothing a creator typed lives in this row.
/// </para>
/// <para>
/// <strong>Never deleted when the provider stops listing it.</strong> It becomes
/// <see cref="PublishingProfileStatus.Removed"/>: a publishing record that points here must still be able to
/// say where it went.
/// </para>
/// </remarks>
public class PublishingProfile : IWorkspaceOwned
{
    public Guid Id { get; set; }

    /// <summary>Stamped by <c>WorkspaceOwnershipInterceptor</c> from the resolved context, never by hand.</summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>Which provider holds it, as a stable key. Not a credential and not an id.</summary>
    public string ProviderKey { get; set; } = string.Empty;

    public PublishingPlatform Platform { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>An IANA zone this server recognises, or null.</summary>
    public string? TimeZoneId { get; set; }

    public PublishingProfileStatus Status { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>When the provider last listed it. Stops moving once it is <see cref="PublishingProfileStatus.Removed"/>.</summary>
    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>
/// The integration record that maps a provider's profile to CreatorPantry's (external.md). Workspace-owned.
/// </summary>
/// <remarks>
/// The only table that holds a publishing provider's identifiers. One row per profile, unique per provider
/// within the workspace, so reading the same provider profile twice is one <see cref="PublishingProfile"/> and
/// a retry never makes a second.
/// </remarks>
public class ExternalPublishingProfileReference : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid PublishingProfileId { get; set; }

    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>The provider's id for the profile. Never returned by a facade and never logged.</summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>The provider's id for the account or organization that holds it.</summary>
    public string ExternalAccountId { get; set; } = string.Empty;
}

/// <summary>
/// When a workspace's provider was last asked for its profiles, and how that went. Workspace-owned.
/// </summary>
/// <remarks>
/// <para>
/// The synchronization status external.md asks to be kept in a dedicated record. It is what stops a page view
/// spending the provider's small quota: a read is served from storage while the last good one is fresh, and
/// the provider is left alone until <see cref="RetryNotBefore"/> after it throttled or refused.
/// </para>
/// <para>
/// One row per workspace and provider. It records outcomes only — never a response, a header or a message.
/// </para>
/// </remarks>
public class PublishingProviderSync : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public string ProviderKey { get; set; } = string.Empty;

    public DateTimeOffset LastAttemptAt { get; set; }

    public DateTimeOffset? LastSucceededAt { get; set; }

    /// <summary>How the last attempt ended. Never <see cref="PublishingConnectionStatus.NotConfigured"/>: that is not an attempt.</summary>
    public PublishingConnectionStatus LastOutcome { get; set; }

    /// <summary>The provider is not asked again before this, or null when it may be.</summary>
    public DateTimeOffset? RetryNotBefore { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
