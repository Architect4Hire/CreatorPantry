using CreatorPantry.Domain.Modules.Publishing.Managers;

namespace CreatorPantry.Domain.Modules.Publishing.Gateways;

/// <summary>What is configured on this host, without anything that is secret.</summary>
/// <param name="Configured">False when this host has no publishing provider at all.</param>
/// <param name="ProviderKey">The provider's stable key, when one is configured.</param>
/// <param name="WorkspaceId">
/// The one workspace the configured credential belongs to. A provider key acts for a single account at the
/// provider, so it is configured together with the workspace that account is; every other workspace is
/// treated as having no provider.
/// <para>
/// <strong>An id, never a slug.</strong> A slug is made from a name a user chooses, and one that is free —
/// configured before its workspace exists, or after it was deleted — can be taken by anybody who creates a
/// workspace called that. Nobody chooses a workspace's id.
/// </para>
/// </param>
public sealed record PublishingProviderDescriptor(bool Configured, string? ProviderKey = null, Guid? WorkspaceId = null)
{
    public static readonly PublishingProviderDescriptor None = new(false);
}

/// <summary>How one read of the provider's profiles ended.</summary>
public enum PublishingProfileFetchOutcome
{
    Fetched = 1,

    /// <summary>No provider is configured on this host. Terminal.</summary>
    NotConfigured = 2,

    /// <summary>The provider refused the credential, or says it may not read profiles. Terminal until it is replaced.</summary>
    CredentialRejected = 3,

    /// <summary>Throttled. Worth another attempt, after the wait.</summary>
    RateLimited = 4,

    /// <summary>Unreachable, timed out, failed on its side, or answered with something that could not be read.</summary>
    Unavailable = 5,
}

/// <summary>
/// One profile as the provider reported it, already in the application's own terms.
/// </summary>
/// <remarks>
/// The two external ids are the only provider values that travel past the gateway, and they go to exactly one
/// place: the integration record that maps them to CreatorPantry's own id. They are never returned by a facade.
/// </remarks>
/// <param name="ExternalId">The provider's id for the profile.</param>
/// <param name="ExternalAccountId">The provider's id for the account or organization that holds it.</param>
/// <param name="TimeZoneId">As the provider gave it, unvalidated: whether it is a zone this server knows is Business's to say.</param>
public sealed record ProviderPublishingProfile(
    string ExternalId,
    string ExternalAccountId,
    PublishingPlatform Platform,
    string DisplayName,
    string? TimeZoneId,
    PublishingProfileStatus Status);

/// <param name="Profiles">Present exactly when the outcome is <see cref="PublishingProfileFetchOutcome.Fetched"/>.</param>
/// <param name="RetryAfter">How long the provider asked to be left alone, when it said.</param>
/// <param name="Truncated">
/// True when the provider had more than one read takes and the list was cut short. What is listed is real;
/// what is missing is not known to be gone, so nothing may be marked removed on the strength of it.
/// </param>
public sealed record PublishingProfileFetch(
    PublishingProfileFetchOutcome Outcome,
    IReadOnlyList<ProviderPublishingProfile> Profiles,
    TimeSpan? RetryAfter = null,
    bool Truncated = false)
{
    public static PublishingProfileFetch Of(PublishingProfileFetchOutcome outcome, TimeSpan? retryAfter = null) =>
        new(outcome, [], retryAfter);
}

/// <summary>
/// The boundary to a publishing provider's list of profiles, in terms of the application's own types
/// (PUB-001, INT-030).
/// </summary>
/// <remarks>
/// <para>
/// A gateway by backend.md's definition: it owns the provider call and nothing else. No provider DTO, status
/// code, header or message crosses it; every way the call can end is one of
/// <see cref="PublishingProfileFetchOutcome"/>.
/// </para>
/// <para>
/// <strong>It never throws for a provider failure.</strong> A caller cancelling is the one exception that
/// propagates.
/// </para>
/// </remarks>
public interface IPublishingProfileGateway
{
    /// <summary>Which provider this host is configured for, and whose it is.</summary>
    PublishingProviderDescriptor Describe();

    /// <summary>Reads every profile the configured credential can see. Read-only at the provider.</summary>
    Task<PublishingProfileFetch> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The gateway on a host with no publishing provider: it says so, and calls nothing.
/// </summary>
/// <remarks>
/// Registered by default, so a clean clone starts and every workspace reads a stable "not configured" rather
/// than a host that refuses to boot for want of a credential nobody has yet (external.md).
/// </remarks>
public sealed class UnconfiguredPublishingProfileGateway : IPublishingProfileGateway
{
    public PublishingProviderDescriptor Describe() => PublishingProviderDescriptor.None;

    public Task<PublishingProfileFetch> FetchAsync(CancellationToken cancellationToken) =>
        Task.FromResult(PublishingProfileFetch.Of(PublishingProfileFetchOutcome.NotConfigured));
}
