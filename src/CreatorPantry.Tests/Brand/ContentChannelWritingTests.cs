using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// AF.6.2: every channel has a writing profile, and the validator counts a body the way that channel does —
/// at, under and over its limit, including where one visible character is several code points.
/// </summary>
public sealed class ContentChannelWritingTests
{
    /// <summary>Four people joined by three zero-width joiners: one visible character, seven code points.</summary>
    private const string Family = "\U0001F469‍\U0001F469‍\U0001F467‍\U0001F466";

    private const string ThumbsUp = "\U0001F44D";

    private const string ThumbsUpMediumSkin = "\U0001F44D\U0001F3FD";

    private const string IrishFlag = "\U0001F1EE\U0001F1EA";

    private const string KeycapOne = "1️⃣";

    private static readonly ContentChannelProfileCatalog Profiles = new();

    private static ContentChannelProfile Profile(string key) => Profiles.Find(key)!;

    private static string Letters(int count) => new('a', count);

    public static TheoryData<string> ChannelKeys => [.. new ContentChannelCatalog().All.Select(channel => channel.Key)];

    // ---- The catalogue ----

    [Fact]
    public void Every_channel_has_exactly_one_profile_and_no_profile_names_a_channel_that_does_not_exist()
    {
        var channels = new ContentChannelCatalog().All.Select(channel => channel.Key).Order(StringComparer.Ordinal);
        var profiled = Profiles.All.Select(profile => profile.ChannelKey).Order(StringComparer.Ordinal);

        Assert.Equal(channels, profiled);
        Assert.Null(Profiles.Find("myspace"));
        Assert.Null(Profiles.Find("Instagram"));
    }

    [Fact]
    public void The_limits_are_the_approved_ones()
    {
        Assert.Equal(
            new Dictionary<string, int>
            {
                ["blog"] = 600,
                ["newsletter"] = 400,
                ["instagram"] = 2200,
                ["tiktok"] = 2200,
                ["pinterest"] = 800,
                ["facebook"] = 2000,
                ["x"] = 280,
                ["threads"] = 500,
            },
            Profiles.All.ToDictionary(profile => profile.ChannelKey, profile => profile.MaxLength));

        Assert.Equal(10, Profile("instagram").MaxHashtags);
        Assert.Equal(20, Profile("instagram").MaxMentions);
        Assert.Equal(ChannelOutputKind.BlogIntro, Profile("blog").OutputKind);
        Assert.Equal(ChannelOutputKind.NewsletterBlurb, Profile("newsletter").OutputKind);
        Assert.Equal(ChannelOutputKind.PinDescription, Profile("pinterest").OutputKind);
    }

    [Fact]
    public void A_limit_that_is_ours_is_never_labelled_as_the_platforms()
    {
        var editorial = Profiles.All
            .Where(profile => profile.LimitOrigin == ChannelLimitOrigin.Editorial)
            .Select(profile => profile.ChannelKey)
            .Order(StringComparer.Ordinal);

        Assert.Equal(new[] { "blog", "facebook", "newsletter" }, editorial);
    }

    // ---- At, under and over, per channel ----

    [Theory]
    [MemberData(nameof(ChannelKeys))]
    public void A_body_at_or_under_the_limit_is_within_it_and_one_more_is_over(string key)
    {
        var profile = Profile(key);
        var limit = profile.MaxLength;

        var under = ContentChannelWriting.Measure(profile, Letters(limit - 1));
        var at = ContentChannelWriting.Measure(profile, Letters(limit));
        var over = ContentChannelWriting.Measure(profile, Letters(limit + 1));

        Assert.Equal(limit - 1, under.Count);
        Assert.False(under.IsOverLimit);
        Assert.Empty(under.Findings);

        Assert.Equal(limit, at.Count);
        Assert.False(at.IsOverLimit);
        Assert.True(at.IsAcceptable);

        Assert.Equal(limit + 1, over.Count);
        Assert.True(over.IsOverLimit);
        Assert.False(over.IsAcceptable);

        var finding = Assert.Single(over.Findings);
        Assert.Equal(ChannelWritingFindingCode.OverLength, finding.Code);
        Assert.Equal(ChannelWritingSeverity.Error, finding.Severity);
        Assert.Equal(limit + 1, finding.Actual);
        Assert.Equal(limit, finding.Allowed);

        Assert.Equal(key, at.ChannelKey);
        Assert.Equal(limit, at.Limit);
        Assert.Equal(Profiles.Version, at.ProfileVersion);
    }

    // ---- Multi-codepoint characters, per counting rule ----

    [Theory]
    [InlineData("instagram")]
    [InlineData("tiktok")]
    [InlineData("pinterest")]
    [InlineData("facebook")]
    public void A_channel_counted_in_UTF16_units_charges_a_joined_emoji_for_every_unit(string key)
    {
        var profile = Profile(key);

        // Four surrogate pairs and three joiners.
        Assert.Equal(11, ContentChannelWriting.Count(profile.Counting, Family));
        Assert.Equal(2, ContentChannelWriting.Count(profile.Counting, "é"));

        Assert.False(ContentChannelWriting.Measure(profile, Letters(profile.MaxLength - 11) + Family).IsOverLimit);
        Assert.True(ContentChannelWriting.Measure(profile, Letters(profile.MaxLength - 10) + Family).IsOverLimit);
    }

    [Theory]
    [InlineData("blog")]
    [InlineData("newsletter")]
    public void An_owned_channel_counts_what_a_reader_would_call_characters(string key)
    {
        var profile = Profile(key);

        Assert.Equal(1, ContentChannelWriting.Count(profile.Counting, Family));
        Assert.Equal(1, ContentChannelWriting.Count(profile.Counting, "é"));
        Assert.Equal(1, ContentChannelWriting.Count(profile.Counting, IrishFlag));

        Assert.False(ContentChannelWriting.Measure(profile, Letters(profile.MaxLength - 1) + Family).IsOverLimit);
        Assert.True(ContentChannelWriting.Measure(profile, Letters(profile.MaxLength) + Family).IsOverLimit);
    }

    [Theory]
    [InlineData("cake", 4)]
    [InlineData("180°C", 5)]                     // A degree sign is a symbol, not an emoji.
    [InlineData("crème", 5)]                     // Composed.
    [InlineData("crème", 5)]                    // Decomposed: NFC-normalised before counting.
    [InlineData("日本", 4)]                   // CJK weighs two each.
    [InlineData(ThumbsUp, 2)]
    [InlineData(ThumbsUpMediumSkin, 2)]               // One emoji, whatever modifies it.
    [InlineData(Family, 2)]
    [InlineData(IrishFlag, 2)]
    [InlineData(KeycapOne, 2)]
    [InlineData("https://example.com/a/very/long/path/to/a/recipe/that/goes/on?and=on", 23)]
    [InlineData("www.example.com", 23)]
    [InlineData("Recipe: https://example.com/cake.", 8 + 23 + 1)]
    [InlineData("http://a.io and https://b.io", 23 + 5 + 23)]
    public void X_counts_by_its_published_weights(string body, int expected) =>
        Assert.Equal(expected, ContentChannelWriting.Count(ChannelCountingRule.XWeighted, body));

    [Fact]
    public void X_is_at_its_limit_with_an_emoji_weighing_two_and_over_with_one_letter_more()
    {
        var profile = Profile("x");

        var at = ContentChannelWriting.Measure(profile, Letters(278) + Family);
        var over = ContentChannelWriting.Measure(profile, Letters(279) + Family);

        Assert.Equal(280, at.Count);
        Assert.False(at.IsOverLimit);
        Assert.Equal(281, over.Count);
        Assert.True(over.IsOverLimit);

        // A long URL costs 23 however long it is, so a body far over 280 UTF-16 units can still fit.
        var withLink = ContentChannelWriting.Measure(profile, Letters(257) + "https://example.com/" + Letters(400));
        Assert.Equal(280, withLink.Count);
        Assert.False(withLink.IsOverLimit);
    }

    [Theory]
    [InlineData("cake", 4)]
    [InlineData("180°C", 5)]
    [InlineData("crème", 5)]
    [InlineData(ThumbsUp, 4)]                         // F0 9F 91 8D.
    [InlineData(ThumbsUpMediumSkin, 8)]
    [InlineData(IrishFlag, 8)]
    [InlineData(Family, 25)]                          // Four of four bytes, three joiners of three.
    [InlineData(KeycapOne, 7)]                        // '1', then three bytes each for the selector and the mark.
    public void Threads_charges_an_emoji_its_UTF8_bytes(string body, int expected) =>
        Assert.Equal(expected, ContentChannelWriting.Count(ChannelCountingRule.ThreadsEmojiBytes, body));

    [Fact]
    public void Threads_is_at_its_limit_with_a_joined_emoji_costing_twenty_five_and_over_with_one_letter_more()
    {
        var profile = Profile("threads");

        Assert.False(ContentChannelWriting.Measure(profile, Letters(475) + Family).IsOverLimit);
        Assert.Equal(500, ContentChannelWriting.Measure(profile, Letters(475) + Family).Count);
        Assert.True(ContentChannelWriting.Measure(profile, Letters(476) + Family).IsOverLimit);
    }

    // ---- Hashtags, mentions, links, markup ----

    [Fact]
    public void Instagram_takes_ten_hashtags_and_flags_the_eleventh()
    {
        var profile = Profile("instagram");
        string Tags(int count) => string.Join(' ', Enumerable.Range(1, count).Select(index => $"#bake{index}"));

        Assert.Empty(ContentChannelWriting.Measure(profile, "Olive oil cake. " + Tags(10)).Findings);

        var finding = Assert.Single(ContentChannelWriting.Measure(profile, "Olive oil cake. " + Tags(11)).Findings);
        Assert.Equal(ChannelWritingFindingCode.TooManyHashtags, finding.Code);
        Assert.Equal(11, finding.Actual);
        Assert.Equal(10, finding.Allowed);
    }

    [Fact]
    public void Only_a_word_that_starts_with_a_hash_and_holds_a_letter_is_a_hashtag()
    {
        var profile = Profile("blog");

        // A number, a mid-word hash and a URL fragment are not tags, so a channel that allows none is content.
        Assert.Empty(ContentChannelWriting.Measure(
            profile, "Loaf #1 of recipe#2, see https://example.com/bread#method for more.").Findings);

        var finding = Assert.Single(ContentChannelWriting.Measure(profile, "A loaf for #autumn.").Findings);
        Assert.Equal(ChannelWritingFindingCode.TooManyHashtags, finding.Code);
        Assert.Equal(1, finding.Actual);
        Assert.Equal(0, finding.Allowed);
    }

    [Fact]
    public void Threads_takes_one_topic_tag_of_at_most_fifty_characters()
    {
        var profile = Profile("threads");

        Assert.Empty(ContentChannelWriting.Measure(profile, "Soda bread #" + Letters(50)).Findings);

        var tooLong = Assert.Single(ContentChannelWriting.Measure(profile, "Soda bread #" + Letters(51)).Findings);
        Assert.Equal(ChannelWritingFindingCode.HashtagTooLong, tooLong.Code);
        Assert.Equal(51, tooLong.Actual);
        Assert.Equal(50, tooLong.Allowed);

        var tooMany = Assert.Single(ContentChannelWriting.Measure(profile, "Soda bread #baking #bread").Findings);
        Assert.Equal(ChannelWritingFindingCode.TooManyHashtags, tooMany.Code);
        Assert.Equal(1, tooMany.Allowed);
    }

    [Fact]
    public void Instagram_takes_twenty_mentions_and_an_email_address_is_not_one()
    {
        var profile = Profile("instagram");
        string Mentions(int count) => string.Join(' ', Enumerable.Range(1, count).Select(index => $"@baker{index}"));

        Assert.Empty(ContentChannelWriting.Measure(profile, Mentions(20) + " write to hello@example.org").Findings);

        var finding = Assert.Single(ContentChannelWriting.Measure(profile, Mentions(21)).Findings);
        Assert.Equal(ChannelWritingFindingCode.TooManyMentions, finding.Code);
        Assert.Equal(21, finding.Actual);
        Assert.Equal(20, finding.Allowed);
    }

    [Fact]
    public void Threads_takes_five_links_and_refuses_a_sixth()
    {
        var profile = Profile("threads");
        string Links(int count) => string.Join(' ', Enumerable.Range(1, count).Select(index => $"https://example.com/{index}"));

        Assert.Empty(ContentChannelWriting.Measure(profile, Links(5)).Findings);

        var finding = Assert.Single(ContentChannelWriting.Measure(profile, Links(6)).Findings);
        Assert.Equal(ChannelWritingFindingCode.TooManyLinks, finding.Code);
        Assert.Equal(ChannelWritingSeverity.Error, finding.Severity);
        Assert.Equal(6, finding.Actual);
        Assert.Equal(5, finding.Allowed);
    }

    [Theory]
    [InlineData("instagram")]
    [InlineData("tiktok")]
    [InlineData("pinterest")]
    public void A_link_where_the_channel_carries_it_elsewhere_is_a_warning_and_not_a_failure(string key)
    {
        var result = ContentChannelWriting.Measure(Profile(key), "The recipe: https://example.com/cake");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(ChannelWritingFindingCode.LinkDiscouraged, finding.Code);
        Assert.Equal(ChannelWritingSeverity.Warning, finding.Severity);
        Assert.True(result.IsAcceptable);
    }

    [Theory]
    [InlineData("blog")]
    [InlineData("newsletter")]
    [InlineData("facebook")]
    [InlineData("x")]
    public void A_link_is_unremarkable_where_links_belong(string key) =>
        Assert.Empty(ContentChannelWriting.Measure(Profile(key), "The recipe: https://example.com/cake").Findings);

    [Theory]
    [InlineData("A <b>warm</b> loaf.", 2)]
    [InlineData("Get [the recipe](https://example.com/cake).", 1)]
    public void Markup_in_a_plain_text_body_is_a_warning(string body, int expected)
    {
        var finding = Assert.Single(
            ContentChannelWriting.Measure(Profile("facebook"), body).Findings,
            found => found.Code == ChannelWritingFindingCode.MarkupFound);

        Assert.Equal(ChannelWritingSeverity.Warning, finding.Severity);
        Assert.Equal(expected, finding.Actual);
    }

    [Fact]
    public void An_inequality_is_not_markup()
    {
        Assert.Empty(ContentChannelWriting.Measure(Profile("facebook"), "Bake until 3 < 5 and 200 > 180.").Findings);
    }

    // ---- What a revision stores ----

    [Fact]
    public void A_measurement_maps_onto_the_limit_result_a_revision_stores_and_passes_its_own_shape_check()
    {
        var profile = Profile("x");

        foreach (var (body, status) in new[]
        {
            (Letters(280), SocialLimitStatus.Within),
            (Letters(281), SocialLimitStatus.Over),
        })
        {
            var stored = SocialLimitResult.From(ContentChannelWriting.Measure(profile, body));

            Assert.Equal(status, stored.Status);
            Assert.Equal(body.Length, stored.CharacterCount);
            Assert.Equal(280, stored.CharacterLimit);
            Assert.Equal(ContentChannelProfileCatalog.CurrentVersion, stored.ChannelProfileVersion);
            Assert.Empty(SocialPackageInputChecks.Edited(
                new SocialEditedRevisionInput(Guid.NewGuid(), "x", body, stored, null)));
        }
    }
}
