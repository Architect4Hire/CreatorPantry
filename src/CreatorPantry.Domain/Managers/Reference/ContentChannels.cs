using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Domain.Managers.Reference;

/// <summary>
/// One content channel CreatorPantry knows how to write for. Platform reference data: global, no workspace,
/// and identified by a stable <see cref="Key"/> that is stored by workspace-owned records.
/// </summary>
/// <param name="Key">Lowercase, stable, never reused for a different channel.</param>
/// <param name="DisplayName">What a creator sees.</param>
/// <param name="IsActive">
/// False once a channel is retired. A retired channel stays readable so records that already carry its key keep
/// resolving, and is refused as a new choice, exactly as retired vocabulary entries are.
/// </param>
public sealed record ContentChannel(string Key, string DisplayName, bool IsActive = true);

/// <summary>
/// The one source of truth for content-channel keys (12.1). Every module that stores or validates a channel key
/// asks this rather than holding its own literals.
/// </summary>
/// <remarks>
/// Channel <em>constraints</em> — length, markup, image ratio — belong to channel profiles and adapters
/// (content.md), not here. This holds identity only.
/// </remarks>
public interface IContentChannelCatalog
{
    /// <summary>Every channel, active and retired, in display order.</summary>
    IReadOnlyList<ContentChannel> All { get; }

    /// <summary>The channel with this exact key, or null when no such channel has ever existed.</summary>
    ContentChannel? Find(string key);
}

/// <summary>
/// The catalogue as configuration in code: versioned with the source, reviewed like source, and needing no
/// migration to change. Not user-managed — a user-managed list would be separate scope.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Provisional list.</strong> The six channels are the social platforms of SOC-001 — Instagram,
/// TikTok, Pinterest, Facebook, X and Threads — because the requirement text behind DATA-RQ-001 to 003 is not
/// in the repository. "Blog Intro", the seventh SOC-001 output, is a derivative written for the creator's own
/// blog rather than a channel. Correcting the list is an edit here plus <c>ContentChannelCatalogTests</c>; no
/// stored data exists under these keys yet.
/// </para>
/// <para>
/// Weekly themes, the other half of 12.1, are deliberately absent: nothing in the repository names them.
/// </para>
/// </remarks>
public sealed class ContentChannelCatalog : IContentChannelCatalog
{
    private static readonly ContentChannel[] Default =
    [
        new("instagram", "Instagram"),
        new("tiktok", "TikTok"),
        new("pinterest", "Pinterest"),
        new("facebook", "Facebook"),
        new("x", "X"),
        new("threads", "Threads"),
    ];

    private readonly Dictionary<string, ContentChannel> _byKey;

    public ContentChannelCatalog()
        : this(Default)
    {
    }

    /// <summary>For tests that need a retired channel. Keys must be unique.</summary>
    public ContentChannelCatalog(IEnumerable<ContentChannel> channels)
    {
        All = [.. channels];
        _byKey = All.ToDictionary(channel => channel.Key, StringComparer.Ordinal);
    }

    public IReadOnlyList<ContentChannel> All { get; }

    public ContentChannel? Find(string key) => _byKey.GetValueOrDefault(key);
}

public static class ContentChannelCatalogServiceCollectionExtensions
{
    /// <summary>
    /// Registers the default catalogue unless a host or test has already registered its own. Built with an
    /// explicit factory on purpose: <see cref="ContentChannelCatalog"/> has a second, list-taking constructor,
    /// and the container would pick that one and resolve an empty list, producing a catalogue with no channels.
    /// </summary>
    public static IServiceCollection AddContentChannelCatalog(this IServiceCollection services)
    {
        services.TryAddSingleton<IContentChannelCatalog>(_ => new ContentChannelCatalog());

        return services;
    }
}
