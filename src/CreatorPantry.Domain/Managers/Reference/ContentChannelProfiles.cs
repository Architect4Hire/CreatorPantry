using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Domain.Managers.Reference;

/// <summary>What a channel's post is. Presentation and prompt selection read this; no limit depends on it.</summary>
public enum ChannelOutputKind
{
    Caption = 1,
    PinDescription = 2,
    Post = 3,
    BlogIntro = 4,
    NewsletterBlurb = 5,
}

/// <summary>How a channel counts a body's length. The rule is the platform's wherever it publishes one.</summary>
public enum ChannelCountingRule
{
    /// <summary>
    /// UTF-16 code units — <c>string.Length</c>. What TikTok documents, and the choice for a platform that
    /// states a limit in "characters" without a unit: it is never smaller than a count of code points or of
    /// visible characters, so a body that passes here cannot be one the platform refuses for length.
    /// </summary>
    Utf16Units = 1,

    /// <summary>What a reader would call characters: one per grapheme cluster. For limits that are ours.</summary>
    TextElements = 2,

    /// <summary>X's weighted count; see <see cref="ContentChannelWriting"/>.</summary>
    XWeighted = 3,

    /// <summary>Threads' count, in which an emoji costs its UTF-8 byte length.</summary>
    ThreadsEmojiBytes = 4,
}

/// <summary>Whose rule a length limit is.</summary>
public enum ChannelLimitOrigin
{
    /// <summary>The platform's own, cited where the profile is declared.</summary>
    Platform = 1,

    /// <summary>
    /// A CreatorPantry editorial ceiling. Never to be presented as the platform's: either the channel is the
    /// creator's own, or no first-party source for a platform limit could be found.
    /// </summary>
    Editorial = 2,
}

/// <summary>Whether a URL belongs in a channel's body.</summary>
public enum ChannelLinkPolicy
{
    Allowed = 1,

    /// <summary>
    /// Raised as a warning, never a failure. A CreatorPantry choice, not a platform rule: on these channels
    /// the link belongs in a field of its own or in the profile, and nothing first-party was found to say a
    /// URL in the body is refused.
    /// </summary>
    Flagged = 2,
}

/// <summary>
/// One channel's deterministic writing profile (AF.6.2): what it produces and what a body for it may hold.
/// </summary>
/// <param name="ChannelKey">A <see cref="ContentChannel.Key"/>.</param>
/// <param name="MaxLength">The most a body may count, by <paramref name="Counting"/>.</param>
/// <param name="MaxHashtags">Null for no cap; zero where hashtags do not belong at all.</param>
/// <param name="MaxMentions">Null for no cap.</param>
/// <param name="MaxLinks">Null for no cap.</param>
/// <param name="HashtagMaxLength">The longest one tag's text may be, or null. Only Threads states one.</param>
/// <remarks>
/// Limits are code and never prompt text (content.md). A prompt may be told what a profile says; it is the
/// validator, after generation, that decides whether a body meets it.
/// </remarks>
public sealed record ContentChannelProfile(
    string ChannelKey,
    ChannelOutputKind OutputKind,
    int MaxLength,
    ChannelCountingRule Counting,
    ChannelLimitOrigin LimitOrigin,
    ChannelLinkPolicy LinkPolicy,
    int? MaxHashtags = null,
    int? MaxMentions = null,
    int? MaxLinks = null,
    int? HashtagMaxLength = null);

/// <summary>The writing profile for each channel in <see cref="IContentChannelCatalog"/>.</summary>
public interface IContentChannelProfileCatalog
{
    /// <summary>
    /// Changes whenever any profile or counting rule does, so a stored measurement can be explained after the
    /// rules move on.
    /// </summary>
    string Version { get; }

    IReadOnlyList<ContentChannelProfile> All { get; }

    /// <summary>The profile for this exact key, or null when the channel has none.</summary>
    ContentChannelProfile? Find(string channelKey);
}

/// <summary>
/// The profiles as configuration in code, like the catalogue beside them: versioned with the source and
/// reviewed like source.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every platform figure below was read from the cited first-party page on 2026-10-10.</strong> They
/// move; re-read the page before changing one, and bump <see cref="CurrentVersion"/> when you do.
/// </para>
/// <para>
/// <strong>Short-form by decision (B-27, confirmed 2026-10-10).</strong> <c>blog</c> yields an intro and
/// <c>newsletter</c> a blurb. The long-form article stays with <c>content.editorial-package</c>.
/// </para>
/// </remarks>
public sealed class ContentChannelProfileCatalog : IContentChannelProfileCatalog
{
    public const string CurrentVersion = "1.0.0";

    private static readonly ContentChannelProfile[] Default =
    [
        // The creator's own channels have no platform to set a limit. Both ceilings are editorial, and
        // hashtags do not belong in either.
        new("blog", ChannelOutputKind.BlogIntro, 600, ChannelCountingRule.TextElements,
            ChannelLimitOrigin.Editorial, ChannelLinkPolicy.Allowed, MaxHashtags: 0),
        new("newsletter", ChannelOutputKind.NewsletterBlurb, 400, ChannelCountingRule.TextElements,
            ChannelLimitOrigin.Editorial, ChannelLinkPolicy.Allowed, MaxHashtags: 0),

        // "Maximum 2200 characters, 30 hashtags, and 20 @ tags."
        // https://developers.facebook.com/docs/instagram-platform/instagram-graph-api/reference/ig-user/media/
        // The unit of "characters" is not stated, hence UTF-16 units. The hashtag cap of 10 is the product
        // owner's (2026-10-10), not Instagram's: the reference still says 30, while Instagram was widely
        // reported in December 2025 to have cut the in-app cap to 5 — an announcement not verified against a
        // first-party page here. Revisit this number if the reference changes.
        new("instagram", ChannelOutputKind.Caption, 2200, ChannelCountingRule.Utf16Units,
            ChannelLimitOrigin.Platform, ChannelLinkPolicy.Flagged, MaxHashtags: 10, MaxMentions: 20),

        // post_info.title: "The maximum length is 2200 in UTF-16 runes."
        // https://developers.tiktok.com/doc/content-posting-api-reference-direct-post
        // The video caption's limit. A photo post's description allows 4000, so 2200 is valid for both.
        new("tiktok", ChannelOutputKind.Caption, 2200, ChannelCountingRule.Utf16Units,
            ChannelLimitOrigin.Platform, ChannelLinkPolicy.Flagged),

        // Description: "Enter up to 800 characters". A pin carries its link in a field of its own.
        // https://help.pinterest.com/en/article/review-pin-specs
        new("pinterest", ChannelOutputKind.PinDescription, 800, ChannelCountingRule.Utf16Units,
            ChannelLimitOrigin.Platform, ChannelLinkPolicy.Flagged),

        // Editorial. The 63,206 figure repeated everywhere for Facebook posts could not be found on any
        // first-party page, so it is not cited here as Facebook's limit.
        new("facebook", ChannelOutputKind.Post, 2000, ChannelCountingRule.Utf16Units,
            ChannelLimitOrigin.Editorial, ChannelLinkPolicy.Allowed),

        // 280 weighted characters; see ContentChannelWriting for the weights.
        // https://docs.x.com/fundamentals/counting-characters
        new("x", ChannelOutputKind.Post, 280, ChannelCountingRule.XWeighted,
            ChannelLimitOrigin.Platform, ChannelLinkPolicy.Allowed),

        // "Text posts are limited to 500 characters. Emojis are counted as the number of UTF-8 bytes." Five
        // links or fewer; one topic tag per post, of 1 to 50 characters.
        // https://developers.facebook.com/docs/threads/posts/
        new("threads", ChannelOutputKind.Post, 500, ChannelCountingRule.ThreadsEmojiBytes,
            ChannelLimitOrigin.Platform, ChannelLinkPolicy.Allowed, MaxHashtags: 1, MaxLinks: 5, HashtagMaxLength: 50),
    ];

    private readonly Dictionary<string, ContentChannelProfile> _byKey;

    public ContentChannelProfileCatalog()
        : this(Default)
    {
    }

    /// <summary>For tests that need a profile the default set does not have. Keys must be unique.</summary>
    public ContentChannelProfileCatalog(IEnumerable<ContentChannelProfile> profiles)
    {
        All = [.. profiles];
        _byKey = All.ToDictionary(profile => profile.ChannelKey, StringComparer.Ordinal);
    }

    public string Version => CurrentVersion;

    public IReadOnlyList<ContentChannelProfile> All { get; }

    public ContentChannelProfile? Find(string channelKey) => _byKey.GetValueOrDefault(channelKey);
}

public static class ContentChannelProfileServiceCollectionExtensions
{
    /// <summary>
    /// Registers the default profiles unless a host or test has already registered its own. An explicit
    /// factory for the same reason <c>AddContentChannelCatalog</c> uses one: the list-taking constructor
    /// would otherwise be chosen and resolve an empty list.
    /// </summary>
    public static IServiceCollection AddContentChannelProfiles(this IServiceCollection services)
    {
        services.TryAddSingleton<IContentChannelProfileCatalog>(_ => new ContentChannelProfileCatalog());

        return services;
    }
}
