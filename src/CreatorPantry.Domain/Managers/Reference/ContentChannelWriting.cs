using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CreatorPantry.Domain.Managers.Reference;

/// <summary>What a body got wrong against its channel's profile.</summary>
public enum ChannelWritingFindingCode
{
    OverLength = 1,

    /// <summary>More hashtags than the profile allows — including any at all where it allows none.</summary>
    TooManyHashtags = 2,

    TooManyMentions = 3,

    TooManyLinks = 4,

    /// <summary>A URL in a body whose channel carries its link elsewhere. A warning; see <see cref="ChannelLinkPolicy.Flagged"/>.</summary>
    LinkDiscouraged = 5,

    /// <summary>HTML or Markdown link syntax in a body that will be shown as plain text.</summary>
    MarkupFound = 6,

    HashtagTooLong = 7,
}

public enum ChannelWritingSeverity
{
    /// <summary>Worth a creator's glance; the body would still be taken.</summary>
    Warning = 1,

    /// <summary>The body breaks a limit and would be refused or cut by the channel.</summary>
    Error = 2,
}

/// <param name="Actual">What the body has: its count, its number of tags, the longest tag's length.</param>
/// <param name="Allowed">What the profile allows, or null where the finding is not a comparison.</param>
public sealed record ChannelWritingFinding(
    ChannelWritingFindingCode Code, ChannelWritingSeverity Severity, int Actual, int? Allowed = null);

/// <summary>
/// One body measured against one profile. A measurement only: nothing here changes the body, and an
/// over-limit one is reported, never trimmed.
/// </summary>
/// <param name="Count">The body's length as the channel counts it, which need not be its string length.</param>
public sealed record ChannelWritingResult(
    string ChannelKey,
    int Count,
    int Limit,
    string ProfileVersion,
    IReadOnlyList<ChannelWritingFinding> Findings)
{
    public bool IsOverLimit => Count > Limit;

    /// <summary>True when nothing found is an error. Warnings do not count against a body.</summary>
    public bool IsAcceptable => Findings.All(finding => finding.Severity != ChannelWritingSeverity.Error);
}

/// <summary>
/// Scores a body against a <see cref="ContentChannelProfile"/> (AF.6.2). Deterministic code with no model
/// in it, so the same body always measures the same under one profile version.
/// </summary>
/// <remarks>
/// <para>
/// <strong>X's weighted count</strong> follows the published twitter-text configuration, version 3
/// (<c>https://raw.githubusercontent.com/twitter/twitter-text/master/config/v3.json</c>, explained at
/// <c>https://docs.x.com/fundamentals/counting-characters</c>): the text is NFC-normalised; a code point in
/// one of four ranges weighs 100 and any other 200; an emoji sequence weighs 200 however it is composed; a
/// URL weighs 23 × 100; and the total is divided by 100 against a limit of 280.
/// </para>
/// <para>
/// <strong>Two approximations, both stated rather than hidden.</strong> .NET has no emoji property, so an
/// emoji is a grapheme cluster holding a code point from the pictographic blocks, an emoji variation selector
/// or a keycap mark. That over-recognises a few dingbats, which only ever counts a body as longer. And only
/// URLs written with a scheme or <c>www.</c> are recognised: X also treats a bare domain such as
/// <c>example.com</c> as a URL of weight 23, so a bare domain shorter than that is undercounted here.
/// </para>
/// </remarks>
public static partial class ContentChannelWriting
{
    private const int XScale = 100;

    private const int XDefaultWeight = 200;

    private const int XRangeWeight = 100;

    private const int XUrlLength = 23;

    /// <summary>The code point ranges twitter-text v3 weighs at 100. Everything else weighs 200.</summary>
    private static readonly (int Start, int End)[] XLightRanges =
        [(0, 4351), (8192, 8205), (8208, 8223), (8242, 8247)];

    /// <summary>
    /// Code points that present as emoji on their own. The two wide blocks cover the pictographic planes and
    /// the symbol-and-dingbat block; the rest are the emoji scattered through the technical and CJK blocks.
    /// </summary>
    private static readonly (int Start, int End)[] EmojiRanges =
    [
        (0x1F000, 0x1FAFF), (0x2600, 0x27BF),
        (0x231A, 0x231B), (0x23E9, 0x23F3), (0x23F8, 0x23FA), (0x25FD, 0x25FE),
        (0x2B05, 0x2B07), (0x2B1B, 0x2B1C), (0x2B50, 0x2B50), (0x2B55, 0x2B55),
        (0x2934, 0x2935), (0x3030, 0x3030), (0x303D, 0x303D), (0x3297, 0x3297), (0x3299, 0x3299),
    ];

    private const int EmojiVariationSelector = 0xFE0F;

    private const int KeycapMark = 0x20E3;

    public static ChannelWritingResult Measure(
        ContentChannelProfile profile, string body, string profileVersion = ContentChannelProfileCatalog.CurrentVersion)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(body);

        var findings = new List<ChannelWritingFinding>();
        var count = Count(profile.Counting, body);

        if (count > profile.MaxLength)
        {
            findings.Add(new(ChannelWritingFindingCode.OverLength, ChannelWritingSeverity.Error, count, profile.MaxLength));
        }

        var links = UrlPattern().Count(body);

        // A URL's fragment and its user-info are not a hashtag and a mention.
        var prose = UrlPattern().Replace(body, " ");
        var hashtags = HashtagPattern().Matches(prose);
        var mentions = MentionPattern().Count(prose);

        if (profile.MaxHashtags is { } maxHashtags && hashtags.Count > maxHashtags)
        {
            findings.Add(new(ChannelWritingFindingCode.TooManyHashtags, ChannelWritingSeverity.Error, hashtags.Count, maxHashtags));
        }

        // The tag's own text, without its '#'.
        if (profile.HashtagMaxLength is { } maxTagLength
            && hashtags.Select(tag => tag.Length - 1).DefaultIfEmpty(0).Max() is var longest
            && longest > maxTagLength)
        {
            findings.Add(new(ChannelWritingFindingCode.HashtagTooLong, ChannelWritingSeverity.Error, longest, maxTagLength));
        }

        if (profile.MaxMentions is { } maxMentions && mentions > maxMentions)
        {
            findings.Add(new(ChannelWritingFindingCode.TooManyMentions, ChannelWritingSeverity.Error, mentions, maxMentions));
        }

        if (profile.MaxLinks is { } maxLinks && links > maxLinks)
        {
            findings.Add(new(ChannelWritingFindingCode.TooManyLinks, ChannelWritingSeverity.Error, links, maxLinks));
        }

        if (profile.LinkPolicy == ChannelLinkPolicy.Flagged && links > 0)
        {
            findings.Add(new(ChannelWritingFindingCode.LinkDiscouraged, ChannelWritingSeverity.Warning, links));
        }

        // Every channel here shows a body as plain text, so markup would reach readers as its own syntax.
        if (MarkupPattern().Count(body) is > 0 and var markup)
        {
            findings.Add(new(ChannelWritingFindingCode.MarkupFound, ChannelWritingSeverity.Warning, markup));
        }

        return new ChannelWritingResult(profile.ChannelKey, count, profile.MaxLength, profileVersion, findings);
    }

    /// <summary>A body's length under one counting rule.</summary>
    public static int Count(ChannelCountingRule rule, string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        return rule switch
        {
            ChannelCountingRule.Utf16Units => body.Length,
            ChannelCountingRule.TextElements => new StringInfo(body).LengthInTextElements,
            ChannelCountingRule.XWeighted => CountForX(body),
            ChannelCountingRule.ThreadsEmojiBytes => CountForThreads(body),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "That is not a counting rule."),
        };
    }

    private static int CountForX(string body)
    {
        var text = body.Normalize(NormalizationForm.FormC);
        var weight = 0;
        var position = 0;

        foreach (Match url in UrlPattern().Matches(text))
        {
            weight += WeighForX(text.AsSpan(position, url.Index - position));
            weight += XUrlLength * XScale;
            position = url.Index + url.Length;
        }

        weight += WeighForX(text.AsSpan(position));

        return weight / XScale;
    }

    private static int WeighForX(ReadOnlySpan<char> text)
    {
        var weight = 0;

        while (!text.IsEmpty)
        {
            var element = text[..StringInfo.GetNextTextElementLength(text)];

            if (IsEmoji(element))
            {
                // One emoji, one weight, whatever modifiers and joiners it is built from.
                weight += XDefaultWeight;
            }
            else
            {
                foreach (var rune in element.EnumerateRunes())
                {
                    weight += IsIn(XLightRanges, rune.Value) ? XRangeWeight : XDefaultWeight;
                }
            }

            text = text[element.Length..];
        }

        return weight;
    }

    private static int CountForThreads(string body)
    {
        var count = 0;
        var text = body.AsSpan();

        while (!text.IsEmpty)
        {
            var element = text[..StringInfo.GetNextTextElementLength(text)];

            // "Emojis are counted as the number of UTF-8 bytes." The unit for everything else is not stated,
            // so it is UTF-16 units, which never undercounts.
            count += IsEmoji(element) ? Encoding.UTF8.GetByteCount(element) : element.Length;
            text = text[element.Length..];
        }

        return count;
    }

    private static bool IsEmoji(ReadOnlySpan<char> element)
    {
        foreach (var rune in element.EnumerateRunes())
        {
            if (rune.Value is EmojiVariationSelector or KeycapMark || IsIn(EmojiRanges, rune.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIn((int Start, int End)[] ranges, int codePoint)
    {
        foreach (var (start, end) in ranges)
        {
            if (codePoint >= start && codePoint <= end)
            {
                return true;
            }
        }

        return false;
    }

    // Ends on something that is not sentence punctuation, so "see https://example.com/cake." keeps its stop.
    [GeneratedRegex("""(?:https?://|www\.)[^\s<>"']*[^\s<>"'.,;:!?)\]]""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    // A '#' that starts a word, followed by letters, digits or underscores with at least one letter: "#1" is
    // a number, and "recipe#2" is not a tag.
    [GeneratedRegex(@"(?<![\p{L}\p{N}_&])#(?=[\p{N}_]*\p{L})[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex HashtagPattern();

    // An '@' that starts a word, so an email address is not a mention.
    [GeneratedRegex(@"(?<![\p{L}\p{N}_@.])@[A-Za-z0-9_](?:[A-Za-z0-9_.]*[A-Za-z0-9_])?", RegexOptions.CultureInvariant)]
    private static partial Regex MentionPattern();

    // An HTML tag, or a Markdown link.
    [GeneratedRegex(@"</?[A-Za-z][^<>]*>|\[[^\]\r\n]+\]\([^)\s]+\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkupPattern();
}
